using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Nodes;

static class BridgeMessages
{
    public static string Result(string id, JsonNode result) =>
        new JsonObject { ["id"] = id, ["result"] = result }.ToJsonString();

    public static string Data(string id, DateTime asOf, JsonNode messages) =>
        new JsonObject { ["id"] = id, ["asOf"] = asOf, ["result"] = messages }.ToJsonString();

    public static string Error(string? id, string error) =>
        new JsonObject { ["id"] = id, ["error"] = error }.ToJsonString();

    public static string Push(string id, string security, string eventName, JsonNode? data) =>
        new JsonObject { ["id"] = id, ["security"] = security, ["event"] = eventName, ["data"] = data }.ToJsonString();
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

    static readonly string[] Types = [ReferenceData, HistoricalData, Subscribe, Unsubscribe];
    static readonly string[] Periodicities = ["DAILY", "WEEKLY", "MONTHLY", "QUARTERLY", "SEMI_ANNUALLY", "YEARLY"];

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
            Require(Periodicity is null || Periodicities.Contains(Periodicity),
                $"periodicity must be one of {string.Join(", ", Periodicities)}");
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
