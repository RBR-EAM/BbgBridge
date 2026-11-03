using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Caching.Memory;

const int MaxMessageBytes = 64 * 1024;
const int MaxPendingMessagesPerClient = 10_000;
const long MaxCachedCharacters = 100_000_000;

#if DEBUG
var mock = args.Contains("--mock");
#else
var mock = false;
#endif

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args.Where(arg => arg != "--mock").ToArray(),
    ContentRootPath = AppContext.BaseDirectory,
});

var port = builder.Configuration.GetValue("Port", 8765);
var allowedOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? [];
var cacheDuration = TimeSpan.FromSeconds(builder.Configuration.GetValue("CacheSeconds", 3600));
var maxDataPointsPerDay = builder.Configuration.GetValue("MaxDataPointsPerDay", 250_000);

if (allowedOrigins.Length == 0)
    throw new InvalidOperationException("appsettings.json: AllowedOrigins must list the web app origins, e.g. \"https://app.mycompany.internal\".");

if (!mock && allowedOrigins.Any(origin => !origin.StartsWith("https://")))
    throw new InvalidOperationException("appsettings.json: AllowedOrigins must all start with https://");

if (cacheDuration <= TimeSpan.Zero || maxDataPointsPerDay <= 0)
    throw new InvalidOperationException("appsettings.json: CacheSeconds and MaxDataPointsPerDay must be positive.");

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Listen(IPAddress.Loopback, port);
});

var app = builder.Build();

if (mock)
    app.Logger.LogWarning("MOCK MODE: fake data, no Bloomberg connection.");

IBloombergRequests requests = mock ? new FakeBloombergRequests() : new BloombergRequests();
IBloombergSubscriptions subscriptions = mock ? new FakeBloombergSubscriptions() : new BloombergSubscriptions();

var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = MaxCachedCharacters });

var dailyBudget = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
{
    PermitLimit = maxDataPointsPerDay,
    Window = TimeSpan.FromDays(1),
    AutoReplenishment = true,
    QueueLimit = 0,
});

var strictJson = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
};

app.UseWebSockets();

app.Map("/ws", async context =>
{
    if (!IsTrusted(context))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    try
    {
        await Serve(socket, context.RequestAborted);
    }
    catch (Exception e) when (e is WebSocketException or OperationCanceledException)
    {
    }
});

app.Run();

bool IsTrusted(HttpContext context) =>
    context.Connection.RemoteIpAddress is { } remoteIp && IPAddress.IsLoopback(remoteIp)
    && context.Request.Host.Value == $"127.0.0.1:{port}"
    && allowedOrigins.Contains(context.Request.Headers.Origin.ToString());

async Task Serve(WebSocket socket, CancellationToken cancel)
{
    var outbox = Channel.CreateBounded<string>(MaxPendingMessagesPerClient);
    var sending = SendAll(socket, outbox.Reader, cancel);
    WebSocketCloseStatus closeStatus;
    try
    {
        closeStatus = await ReceiveAll(socket, outbox.Writer, cancel);
    }
    finally
    {
        subscriptions.UnsubscribeAll(outbox.Writer);
        outbox.Writer.TryComplete();
        await sending;
    }
    await socket.CloseOutputAsync(closeStatus, null, cancel);
}

async Task SendAll(WebSocket socket, ChannelReader<string> outbox, CancellationToken cancel)
{
    await foreach (var message in outbox.ReadAllAsync(cancel))
        await socket.SendAsync(Encoding.UTF8.GetBytes(message).AsMemory(), WebSocketMessageType.Text, true, cancel);
}

async Task<WebSocketCloseStatus> ReceiveAll(WebSocket socket, ChannelWriter<string> outbox, CancellationToken cancel)
{
    var buffer = new byte[MaxMessageBytes];
    while (true)
    {
        var length = 0;
        ValueWebSocketReceiveResult received;
        do
        {
            if (length == buffer.Length)
                return WebSocketCloseStatus.MessageTooBig;
            received = await socket.ReceiveAsync(buffer.AsMemory(length), cancel);
            length += received.Count;
        }
        while (!received.EndOfMessage);

        if (received.MessageType == WebSocketMessageType.Close)
            return WebSocketCloseStatus.NormalClosure;

        if (received.MessageType == WebSocketMessageType.Binary)
            return WebSocketCloseStatus.InvalidMessageType;

        var reply = await Handle(buffer.AsMemory(0, length), outbox);
        await outbox.WriteAsync(reply, cancel);
    }
}

async Task<string> Handle(ReadOnlyMemory<byte> message, ChannelWriter<string> outbox)
{
    string? id = null;
    try
    {
        var request = JsonSerializer.Deserialize<BridgeRequest>(message.Span, strictJson) ?? throw new JsonException("Empty request");
        id = request.Id;
        request.Validate();

        switch (request.Type)
        {
            case BridgeRequest.Subscribe:
                SpendDailyBudget(request);
                await Task.Run(() => subscriptions.Subscribe(outbox, request));
                return BridgeMessages.Result(request.Id, "subscribed");

            case BridgeRequest.Unsubscribe:
                subscriptions.Unsubscribe(outbox, request.Id);
                return BridgeMessages.Result(request.Id, "unsubscribed");

            default:
                var data = await Task.Run(() => ExecuteWithCache(request));
                return BridgeMessages.Data(request.Id, data.AsOf, data.MessagesJson);
        }
    }
    catch (Exception e)
    {
        return BridgeMessages.Error(id, e.Message);
    }
}

CachedData ExecuteWithCache(BridgeRequest request)
{
    var key = JsonSerializer.Serialize(request with { Id = "", MaxAgeSeconds = null });
    var maxAge = request.MaxAgeSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : cacheDuration;

    if (cache.Get<CachedData>(key) is { } cached && DateTime.UtcNow - cached.AsOf <= maxAge)
        return cached;

    SpendDailyBudget(request);
    var askedAt = DateTime.UtcNow;
    var fresh = new CachedData(askedAt, requests.Execute(request).ToJsonString());
    cache.Set(key, fresh, new MemoryCacheEntryOptions
    {
        Size = fresh.MessagesJson.Length,
        AbsoluteExpirationRelativeToNow = cacheDuration,
    });
    return fresh;
}

void SpendDailyBudget(BridgeRequest request)
{
    using var lease = dailyBudget.AttemptAcquire(request.CountDataPoints());
    if (!lease.IsAcquired)
        throw new InvalidOperationException($"Daily budget of {maxDataPointsPerDay} Bloomberg data points reached");
}

sealed record CachedData(DateTime AsOf, string MessagesJson);
