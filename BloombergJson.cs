using System.Text.Json;
using Bloomberglp.Blpapi;

static class BloombergJson
{
    public static byte[] ToUtf8(Element element) => Utf8Json.Write(json => Write(json, element));

    public static void Write(Utf8JsonWriter json, Element element)
    {
        if (element.IsNull)
        {
            json.WriteNullValue();
        }
        else if (element.IsArray)
        {
            json.WriteStartArray();
            for (var i = 0; i < element.NumValues; i++)
            {
                if (element.IsComplexType)
                    Write(json, element.GetValueAsElement(i));
                else
                    WriteScalar(json, element, i);
            }
            json.WriteEndArray();
        }
        else if (element.IsComplexType)
        {
            json.WriteStartObject();
            for (var i = 0; i < element.NumElements; i++)
            {
                var child = element.GetElement(i);
                json.WritePropertyName(child.Name.ToString());
                Write(json, child);
            }
            json.WriteEndObject();
        }
        else
        {
            WriteScalar(json, element, 0);
        }
    }

    static void WriteScalar(Utf8JsonWriter json, Element element, int index)
    {
        switch (element.Datatype)
        {
            case Schema.Datatype.BOOL:
                json.WriteBooleanValue(element.GetValueAsBool(index));
                break;
            case Schema.Datatype.INT32 or Schema.Datatype.INT64:
                json.WriteNumberValue(element.GetValueAsInt64(index));
                break;
            case Schema.Datatype.FLOAT32 or Schema.Datatype.FLOAT64:
                WriteFiniteOrNull(json, element.GetValueAsFloat64(index));
                break;
            default:
                json.WriteStringValue(element.GetValueAsString(index));
                break;
        }
    }

    static void WriteFiniteOrNull(Utf8JsonWriter json, double value)
    {
        if (double.IsFinite(value))
            json.WriteNumberValue(value);
        else
            json.WriteNullValue();
    }
}
