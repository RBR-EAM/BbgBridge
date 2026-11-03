using System.Diagnostics;
using Bloomberglp.Blpapi;
using SessionOptions = Bloomberglp.Blpapi.SessionOptions;

interface IBloombergRequests
{
    byte[] Execute(BridgeRequest query);
}

sealed class BloombergRequests : IBloombergRequests
{
    const string RefDataService = "//blp/refdata";
    static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);

    readonly Lock _lock = new();
    Session? _session;

    public byte[] Execute(BridgeRequest query)
    {
        lock (_lock)
        {
            try
            {
                _session ??= Connect();
                _session.SendRequest(ToBloombergRequest(_session, query), new CorrelationID());
                return ReadResponse(_session);
            }
            catch
            {
                _session?.Stop();
                _session = null;
                throw;
            }
        }
    }

    static Session Connect()
    {
        var session = new Session(new SessionOptions { ServerHost = "localhost", ServerPort = 8194 });
        if (session.Start() && session.OpenService(RefDataService))
            return session;

        session.Stop();
        throw new InvalidOperationException("Bloomberg unavailable: is the Terminal running and logged in on this PC?");
    }

    static Request ToBloombergRequest(Session session, BridgeRequest query)
    {
        var request = session.GetService(RefDataService).CreateRequest(query.Type);

        foreach (var security in query.Securities!)
            request.Append(Names.Securities, security);

        foreach (var field in query.Fields!)
            request.Append(Names.Fields, field);

        foreach (var (fieldId, value) in query.Overrides ?? new())
        {
            var entry = request.GetElement(Names.Overrides).AppendElement();
            entry.SetElement(Names.FieldId, fieldId);
            entry.SetElement(Names.Value, value);
        }

        if (query.Type == BridgeRequest.HistoricalData)
        {
            request.Set(Names.StartDate, query.StartDate!);
            request.Set(Names.EndDate, query.EndDate!);
            request.Set(Names.PeriodicitySelection, query.Periodicity ?? "DAILY");
        }

        return request;
    }

    static class Names
    {
        public static readonly Name Securities = new("securities");
        public static readonly Name Fields = new("fields");
        public static readonly Name Overrides = new("overrides");
        public static readonly Name FieldId = new("fieldId");
        public static readonly Name Value = new("value");
        public static readonly Name StartDate = new("startDate");
        public static readonly Name EndDate = new("endDate");
        public static readonly Name PeriodicitySelection = new("periodicitySelection");
    }

    static byte[] ReadResponse(Session session) => Utf8Json.Write(json =>
    {
        json.WriteStartArray();
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (clock.Elapsed > RequestTimeout)
                throw new TimeoutException("Bloomberg did not answer in time");

            var bloombergEvent = session.NextEvent(1000);

            if (bloombergEvent.Type == Event.EventType.REQUEST_STATUS)
                throw new InvalidOperationException("Bloomberg request failed");

            if (bloombergEvent.Type is Event.EventType.PARTIAL_RESPONSE or Event.EventType.RESPONSE)
                foreach (Message message in bloombergEvent)
                    BloombergJson.Write(json, message.AsElement);

            if (bloombergEvent.Type == Event.EventType.RESPONSE)
                break;
        }
        json.WriteEndArray();
    });
}
