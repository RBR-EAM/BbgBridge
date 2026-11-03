using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

static class Utf8Json
{
    public static byte[] Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
            write(json);
        return buffer.WrittenSpan.ToArray();
    }
}

static class BridgeMessages
{
    public static byte[] Result(string id, string result) => Utf8Json.Write(json =>
    {
        json.WriteStartObject();
        json.WriteString("id", id);
        json.WriteString("result", result);
        json.WriteEndObject();
    });

    public static byte[] Data(string id, DateTime asOf, byte[] messagesJson) => Utf8Json.Write(json =>
    {
        json.WriteStartObject();
        json.WriteString("id", id);
        json.WriteString("asOf", asOf);
        json.WritePropertyName("result");
        json.WriteRawValue(messagesJson, skipInputValidation: true);
        json.WriteEndObject();
    });

    public static byte[] Error(string? id, string error) => Utf8Json.Write(json =>
    {
        json.WriteStartObject();
        json.WriteString("id", id);
        json.WriteString("error", error);
        json.WriteEndObject();
    });

    public static byte[] Push(string id, string security, string eventName, byte[]? dataJson) => Utf8Json.Write(json =>
    {
        json.WriteStartObject();
        json.WriteString("id", id);
        json.WriteString("security", security);
        json.WriteString("event", eventName);
        json.WritePropertyName("data");
        if (dataJson is null)
            json.WriteNullValue();
        else
            json.WriteRawValue(dataJson, skipInputValidation: true);
        json.WriteEndObject();
    });
}

sealed record BridgeRequest(
    string Id,
    string Type,
    string[]? Securities = null,
    string[]? Fields = null,
    Dictionary<string, string>? Overrides = null,
    string? StartDate = null,
    string? EndDate = null,
    string? Periodicity = null,
    int? MaxAgeSeconds = null)
{
    public const string ReferenceData = "ReferenceDataRequest";
    public const string HistoricalData = "HistoricalDataRequest";
    public const string Subscribe = "Subscribe";
    public const string Unsubscribe = "Unsubscribe";

    const int MaxSecurities = 1000;
    const int MaxFields = 25;
    const int MaxOverrides = 20;
    const int MaxTextLength = 100;
    const int MaxHistoryDays = 3660;
    const int MaxHistoryValues = 1_000_000;

    static readonly string[] Types = [ReferenceData, HistoricalData, Subscribe, Unsubscribe];

    static readonly Dictionary<string, int> PeriodsPerYear = new()
    {
        ["DAILY"] = 261,
        ["WEEKLY"] = 52,
        ["MONTHLY"] = 12,
        ["QUARTERLY"] = 4,
        ["SEMI_ANNUALLY"] = 2,
        ["YEARLY"] = 1,
    };

    public void Validate()
    {
        Require(Types.Contains(Type), $"type must be one of {string.Join(", ", Types)}");
        Require(IsText(Id), $"id must be 1 to {MaxTextLength} characters");
        Require(Type == HistoricalData || (StartDate, EndDate, Periodicity) is (null, null, null),
            "startDate, endDate and periodicity are only for HistoricalDataRequest");
        Require(Type is ReferenceData or HistoricalData || Overrides is null,
            "overrides are only for ReferenceDataRequest and HistoricalDataRequest");
        Require(MaxAgeSeconds is null || (Type is ReferenceData or HistoricalData && MaxAgeSeconds >= 0),
            "maxAgeSeconds is a number of seconds >= 0, only for ReferenceDataRequest and HistoricalDataRequest");

        if (Type == Unsubscribe)
        {
            Require((Securities, Fields) is (null, null), "Unsubscribe takes only id and type");
            return;
        }

        Require(Securities is { Length: >= 1 and <= MaxSecurities } && Securities.All(IsText),
            $"securities: 1 to {MaxSecurities} texts of 1 to {MaxTextLength} characters");
        Require(Fields is { Length: >= 1 and <= MaxFields } && Fields.All(IsFieldName),
            $"fields: 1 to {MaxFields} names made of letters, digits and _");
        Require(Overrides is null || (Overrides.Count <= MaxOverrides && Overrides.All(o => IsFieldName(o.Key) && IsText(o.Value))),
            $"overrides: at most {MaxOverrides}, each a field name and a text value");

        if (Type == Subscribe)
            Require(Securities.All(security => !security.Contains('?') && !security.StartsWith("//")),
                "a Subscribe security cannot contain '?' or start with '//'");

        if (Type == HistoricalData)
        {
            var days = ParseDate(EndDate).DayNumber - ParseDate(StartDate).DayNumber;
            Require(days is >= 0 and <= MaxHistoryDays, $"endDate must be 0 to {MaxHistoryDays} days after startDate");
            Require(Periodicity is null || PeriodsPerYear.ContainsKey(Periodicity),
                $"periodicity must be one of {string.Join(", ", PeriodsPerYear.Keys)}");

            var periods = Math.Ceiling((days + 1) / 365.25 * PeriodsPerYear[Periodicity ?? "DAILY"]);
            Require(Securities.Length * Fields.Length * periods <= MaxHistoryValues,
                $"too much history in one request: securities × fields × periods must stay under {MaxHistoryValues:N0}; split the request or use a longer periodicity");
        }
    }

    public int CountDataPoints() => Securities!.Length * Fields!.Length;

    static bool IsText(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= MaxTextLength;

    static bool IsFieldName(string? text) =>
        text is { Length: >= 1 and <= MaxTextLength } && text.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    static DateOnly ParseDate(string? text)
    {
        Require(DateOnly.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date),
            "startDate and endDate are required, format yyyyMMdd");
        return date;
    }

    static void Require([DoesNotReturnIf(false)] bool condition, string error)
    {
        if (!condition)
            throw new ArgumentException(error);
    }
}
