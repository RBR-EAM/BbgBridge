using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;

sealed class FakeBloombergRequests : IBloombergRequests
{
    public byte[] Execute(BridgeRequest query) =>
        Utf8Json.Write(json =>
        {
            if (query.Type == BridgeRequest.HistoricalData)
                WriteHistory(json, query);
            else
                WriteReferenceData(json, query);
        });

    static void WriteReferenceData(Utf8JsonWriter json, BridgeRequest query)
    {
        var today = DateTime.UtcNow.Date;
        json.WriteStartArray();
        json.WriteStartObject();
        json.WriteStartArray("securityData");
        foreach (var security in query.Securities!)
        {
            json.WriteStartObject();
            json.WriteString("security", security);
            json.WriteStartArray("fieldExceptions");
            json.WriteEndArray();
            json.WritePropertyName("fieldData");
            FakeData.WriteFields(json, security, query.Fields!, today);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
        json.WriteEndArray();
    }

    static void WriteHistory(Utf8JsonWriter json, BridgeRequest query)
    {
        var start = DateOnly.ParseExact(query.StartDate!, "yyyyMMdd", CultureInfo.InvariantCulture);
        var end = DateOnly.ParseExact(query.EndDate!, "yyyyMMdd", CultureInfo.InvariantCulture);
        var periodicity = query.Periodicity ?? "DAILY";
        json.WriteStartArray();
        foreach (var security in query.Securities!)
        {
            json.WriteStartObject();
            json.WriteStartObject("securityData");
            json.WriteString("security", security);
            json.WriteStartArray("fieldExceptions");
            json.WriteEndArray();
            json.WriteStartArray("fieldData");
            for (var day = start; day <= end; day = NextPeriod(day, periodicity))
            {
                if (periodicity == "DAILY" && day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                    continue;
                json.WriteStartObject();
                json.WriteString("date", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                foreach (var field in query.Fields!)
                    json.WriteNumber(field, FakeData.Value(security, field, day.ToDateTime(TimeOnly.MinValue)));
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    static DateOnly NextPeriod(DateOnly day, string periodicity) => periodicity switch
    {
        "WEEKLY" => day.AddDays(7),
        "MONTHLY" => day.AddMonths(1),
        "QUARTERLY" => day.AddMonths(3),
        "SEMI_ANNUALLY" => day.AddMonths(6),
        "YEARLY" => day.AddYears(1),
        _ => day.AddDays(1),
    };
}

sealed class FakeBloombergSubscriptions : IBloombergSubscriptions
{
    readonly ConcurrentDictionary<(ChannelWriter<byte[]> Outbox, string Id), CancellationTokenSource> _running = new();

    public void Subscribe(ChannelWriter<byte[]> outbox, BridgeRequest request)
    {
        var stop = new CancellationTokenSource();
        if (!_running.TryAdd((outbox, request.Id), stop))
            throw new ArgumentException($"id {request.Id} is already subscribed");
        _ = PushTicks(outbox, request, stop.Token);
    }

    public void Unsubscribe(ChannelWriter<byte[]> outbox, string id)
    {
        if (_running.TryRemove((outbox, id), out var stop))
            stop.Cancel();
    }

    public void UnsubscribeAll(ChannelWriter<byte[]> outbox)
    {
        foreach (var (owner, id) in _running.Keys)
            if (owner == outbox)
                Unsubscribe(outbox, id);
    }

    static async Task PushTicks(ChannelWriter<byte[]> outbox, BridgeRequest request, CancellationToken stop)
    {
        foreach (var security in request.Securities!)
            outbox.TryWrite(BridgeMessages.Push(request.Id, security, "SubscriptionStarted", null));

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        using var stopTimer = stop.Register(timer.Dispose);
        while (await timer.WaitForNextTickAsync())
            foreach (var security in request.Securities)
            {
                var data = Utf8Json.Write(json => FakeData.WriteFields(json, security, request.Fields!, DateTime.UtcNow));
                outbox.TryWrite(BridgeMessages.Push(request.Id, security, "MarketDataEvents", data));
            }
    }
}

static class FakeData
{
    public static void WriteFields(Utf8JsonWriter json, string security, string[] fields, DateTime time)
    {
        json.WriteStartObject();
        foreach (var field in fields)
            json.WriteNumber(field, Value(security, field, time));
        json.WriteEndObject();
    }

    public static double Value(string security, string field, DateTime time)
    {
        var seed = $"{security}|{field}".Aggregate(17, (hash, c) => unchecked(hash * 31 + c));
        var level = 10 + Math.Abs(seed % 99_000) / 100.0;
        var wave = Math.Sin(time.Ticks / (double)TimeSpan.TicksPerSecond / 10 + seed);
        return Math.Round(level * (1 + 0.05 * wave), 2);
    }
}
