using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading.Channels;

sealed class FakeBloombergRequests : IBloombergRequests
{
    public JsonArray Execute(BridgeRequest query) =>
        query.Type == BridgeRequest.HistoricalData ? History(query) : ReferenceData(query);

    static JsonArray ReferenceData(BridgeRequest query)
    {
        var today = DateTime.UtcNow.Date;
        var securityData = new JsonArray();
        foreach (var security in query.Securities!)
            securityData.Add(new JsonObject
            {
                ["security"] = security,
                ["fieldExceptions"] = new JsonArray(),
                ["fieldData"] = FakeData.Fields(security, query.Fields!, today),
            });
        return new JsonArray(new JsonObject { ["securityData"] = securityData });
    }

    static JsonArray History(BridgeRequest query)
    {
        var start = DateOnly.ParseExact(query.StartDate!, "yyyyMMdd", CultureInfo.InvariantCulture);
        var end = DateOnly.ParseExact(query.EndDate!, "yyyyMMdd", CultureInfo.InvariantCulture);
        var periodicity = query.Periodicity ?? "DAILY";
        var messages = new JsonArray();
        foreach (var security in query.Securities!)
        {
            var rows = new JsonArray();
            for (var day = start; day <= end; day = NextPeriod(day, periodicity))
            {
                if (periodicity == "DAILY" && day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                    continue;
                var row = new JsonObject { ["date"] = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
                foreach (var field in query.Fields!)
                    row[field] = FakeData.Value(security, field, day.ToDateTime(TimeOnly.MinValue));
                rows.Add(row);
            }
            messages.Add(new JsonObject
            {
                ["securityData"] = new JsonObject
                {
                    ["security"] = security,
                    ["fieldExceptions"] = new JsonArray(),
                    ["fieldData"] = rows,
                },
            });
        }
        return messages;
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
    readonly ConcurrentDictionary<(ChannelWriter<string> Outbox, string Id), CancellationTokenSource> _running = new();

    public void Subscribe(ChannelWriter<string> outbox, BridgeRequest request)
    {
        var stop = new CancellationTokenSource();
        if (!_running.TryAdd((outbox, request.Id), stop))
            throw new ArgumentException($"id {request.Id} is already subscribed");
        _ = PushTicks(outbox, request, stop.Token);
    }

    public void Unsubscribe(ChannelWriter<string> outbox, string id)
    {
        if (_running.TryRemove((outbox, id), out var stop))
            stop.Cancel();
    }

    public void UnsubscribeAll(ChannelWriter<string> outbox)
    {
        foreach (var (owner, id) in _running.Keys)
            if (owner == outbox)
                Unsubscribe(outbox, id);
    }

    static async Task PushTicks(ChannelWriter<string> outbox, BridgeRequest request, CancellationToken stop)
    {
        foreach (var security in request.Securities!)
            outbox.TryWrite(BridgeMessages.Push(request.Id, security, "SubscriptionStarted", null));

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        using var stopTimer = stop.Register(timer.Dispose);
        while (await timer.WaitForNextTickAsync())
            foreach (var security in request.Securities)
                outbox.TryWrite(BridgeMessages.Push(request.Id, security, "MarketDataEvents", FakeData.Fields(security, request.Fields!, DateTime.UtcNow)));
    }
}

static class FakeData
{
    public static JsonObject Fields(string security, string[] fields, DateTime time)
    {
        var data = new JsonObject();
        foreach (var field in fields)
            data[field] = Value(security, field, time);
        return data;
    }

    public static double Value(string security, string field, DateTime time)
    {
        var seed = $"{security}|{field}".Aggregate(17, (hash, c) => unchecked(hash * 31 + c));
        var level = 10 + Math.Abs(seed % 99_000) / 100.0;
        var wave = Math.Sin(time.Ticks / (double)TimeSpan.TicksPerSecond / 10 + seed);
        return Math.Round(level * (1 + 0.05 * wave), 2);
    }
}
