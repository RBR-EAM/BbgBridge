using System.Text.Json.Nodes;
using Bloomberglp.Blpapi;

static class BloombergJson
{
    public static JsonNode? ToJson(Element element)
    {
        if (element.IsNull)
            return null;

        if (element.IsArray)
        {
            var array = new JsonArray();
            for (var i = 0; i < element.NumValues; i++)
                array.Add(element.IsComplexType ? ToJson(element.GetValueAsElement(i)) : ScalarToJson(element, i));
            return array;
        }

        if (element.IsComplexType)
        {
            var obj = new JsonObject();
            for (var i = 0; i < element.NumElements; i++)
            {
                var child = element.GetElement(i);
                obj[child.Name.ToString()] = ToJson(child);
            }
            return obj;
        }

        return ScalarToJson(element, 0);
    }

    static JsonNode? ScalarToJson(Element element, int index) => element.Datatype switch
    {
        Schema.Datatype.BOOL => element.GetValueAsBool(index),
        Schema.Datatype.INT32 or Schema.Datatype.INT64 => element.GetValueAsInt64(index),
        Schema.Datatype.FLOAT32 or Schema.Datatype.FLOAT64 => FiniteOrNull(element.GetValueAsFloat64(index)),
        _ => element.GetValueAsString(index),
    };

    static JsonNode? FiniteOrNull(double value) => double.IsFinite(value) ? JsonValue.Create(value) : null;
}
