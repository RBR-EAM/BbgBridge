using System.Collections.Immutable;
using System.Threading.Channels;
using Bloomberglp.Blpapi;
using SessionOptions = Bloomberglp.Blpapi.SessionOptions;

interface IBloombergSubscriptions
{
    void Subscribe(ChannelWriter<string> outbox, BridgeRequest request);
    void Unsubscribe(ChannelWriter<string> outbox, string id);
    void UnsubscribeAll(ChannelWriter<string> outbox);
}

sealed class BloombergSubscriptions : IBloombergSubscriptions
{
    const int MaxSubscribedSecurities = 1000;
    static readonly string[] AtMostOneUpdatePerSecond = ["interval=1.0"];

    readonly Lock _lock = new();
    volatile ImmutableDictionary<long, Subscriber> _subscribers = ImmutableDictionary<long, Subscriber>.Empty;
    volatile Session? _session;
    volatile bool _sessionTerminated;
    long _lastCorrelationId;

    sealed record Subscriber(ChannelWriter<string> Outbox, string Id, string Security, Subscription Subscription);

    public void Subscribe(ChannelWriter<string> outbox, BridgeRequest request)
    {
        lock (_lock)
        {
            if (_session is null || _sessionTerminated)
                _session = StartSession();

            if (_subscribers.Values.Any(s => s.Outbox == outbox && s.Id == request.Id))
                throw new ArgumentException($"id {request.Id} is already subscribed");

            if (_subscribers.Count + request.Securities!.Length > MaxSubscribedSecurities)
                throw new ArgumentException($"at most {MaxSubscribedSecurities} live securities in total");

            var added = new Dictionary<long, Subscriber>();
            foreach (var security in request.Securities)
            {
                var correlationId = ++_lastCorrelationId;
                var subscription = new Subscription(security, request.Fields!, AtMostOneUpdatePerSecond, new CorrelationID(correlationId));
                added.Add(correlationId, new Subscriber(outbox, request.Id, security, subscription));
            }

            _subscribers = _subscribers.AddRange(added);
            try
            {
                _session.Subscribe(added.Values.Select(s => s.Subscription).ToList());
            }
            catch
            {
                _subscribers = _subscribers.RemoveRange(added.Keys);
                throw;
            }
        }
    }

    public void Unsubscribe(ChannelWriter<string> outbox, string id) => Remove(s => s.Outbox == outbox && s.Id == id);

    public void UnsubscribeAll(ChannelWriter<string> outbox) => Remove(s => s.Outbox == outbox);

    void Remove(Func<Subscriber, bool> match)
    {
        lock (_lock)
        {
            var removed = _subscribers.Where(entry => match(entry.Value)).ToList();
            if (removed.Count == 0)
                return;

            _subscribers = _subscribers.RemoveRange(removed.Select(entry => entry.Key));
            if (_session is not null && !_sessionTerminated)
                _session.Unsubscribe(removed.Select(entry => entry.Value.Subscription).ToList());
        }
    }

    Session StartSession()
    {
        _subscribers = ImmutableDictionary<long, Subscriber>.Empty;
        _session?.Stop();

        var session = new Session(new SessionOptions { ServerHost = "localhost", ServerPort = 8194 }, OnEvent);
        if (!session.Start())
            throw new InvalidOperationException("Bloomberg unavailable: is the Terminal running and logged in on this PC?");

        _sessionTerminated = false;
        return session;
    }

    void OnEvent(Event bloombergEvent, Session session)
    {
        foreach (Message message in bloombergEvent)
        {
            if (bloombergEvent.Type is Event.EventType.SUBSCRIPTION_DATA or Event.EventType.SUBSCRIPTION_STATUS
                && message.CorrelationID is { } correlationId
                && _subscribers.TryGetValue(correlationId.Value, out var subscriber))
            {
                var data = BloombergJson.ToJson(message.AsElement);
                subscriber.Outbox.TryWrite(BridgeMessages.Push(subscriber.Id, subscriber.Security, message.MessageType.ToString(), data));
            }
            else if (message.MessageType.ToString() == "SessionTerminated" && session == _session)
            {
                _sessionTerminated = true;
                foreach (var orphan in _subscribers.Values)
                    orphan.Outbox.TryWrite(BridgeMessages.Push(orphan.Id, orphan.Security, "SessionTerminated", null));
            }
        }
    }
}
