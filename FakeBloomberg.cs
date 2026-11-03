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
        var validFields = query.Fields!.Where(field => !FakeData.IsInvalid(field)).ToArray();
        var invalidFields = query.Fields!.Where(FakeData.IsInvalid).ToArray();
        json.WriteStartArray();
        json.WriteStartObject();
        json.WriteStartArray("securityData");
        for (var i = 0; i < query.Securities!.Length; i++)
        {
            var security = query.Securities[i];
            var validSecurity = !FakeData.IsInvalid(security);
            json.WriteStartObject();
            json.WriteString("security", security);
            json.WriteStartArray("eidData");
            json.WriteEndArray();
            json.WriteStartArray("fieldExceptions");
            foreach (var field in validSecurity ? invalidFields : [])
            {
                json.WriteStartObject();
                json.WriteString("fieldId", field);
                json.WritePropertyName("errorInfo");
                FakeData.WriteError(json, 9, "BAD_FLD", "Field not valid", "INVALID_FIELD");
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WritePropertyName("fieldData");
            FakeData.WriteFields(json, security, validSecurity ? validFields : [], today);
            if (!validSecurity)
            {
                json.WritePropertyName("securityError");
                FakeData.WriteError(json, 43, "BAD_SEC", "Unknown/Invalid Security", "INVALID_SECURITY");
            }
            json.WriteNumber("sequenceNumber", i);
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
        for (var i = 0; i < query.Securities!.Length; i++)
        {
            var security = query.Securities[i];
            json.WriteStartObject();
            json.WriteStartObject("securityData");
            json.WriteString("security", security);
            json.WriteStartArray("eidData");
            json.WriteEndArray();
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
            json.WriteNumber("sequenceNumber", i);
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
        for (var tick = 0; await timer.WaitForNextTickAsync(); tick++)
        {
            var changedFields = request.Fields!.Where((_, index) => (tick + index) % 3 != 0).ToArray();
            foreach (var security in request.Securities)
            {
                var data = Utf8Json.Write(json => FakeData.WriteFields(json, security, changedFields, DateTime.UtcNow));
                outbox.TryWrite(BridgeMessages.Push(request.Id, security, "MarketDataEvents", data));
            }
        }
    }
}

static class FakeData
{
    public static bool IsInvalid(string name) => name.StartsWith("NOT_A");

    public static void WriteError(Utf8JsonWriter json, int code, string category, string message, string subcategory)
    {
        json.WriteStartObject();
        json.WriteString("source", "mock");
        json.WriteNumber("code", code);
        json.WriteString("category", category);
        json.WriteString("message", message);
        json.WriteString("subcategory", subcategory);
        json.WriteEndObject();
    }

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
