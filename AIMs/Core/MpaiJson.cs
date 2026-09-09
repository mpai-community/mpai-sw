using System;

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mpai.Core;

// Serialisation of MPAI Data Objects to and from JSON, so that a whole object
// — Data AND its Qualifier — can travel in a framework message.
//
// Large binary data is base64-encoded by System.Text.Json. When that becomes
// too heavy, the schemas already provide the alternative: store the bytes and
// carry Length + DataURI instead of inline Data.
public static class MpaiJson
{
    private static readonly JsonSerializerOptions options =
        CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var created =
            new JsonSerializerOptions
            {
                WriteIndented = false,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                IncludeFields = false
            };

        // "oneOf" data types need a converter that selects the branch.
        created.Converters.Add(
            new BasicTextDataConverter());

        return created;
    }

    public static string ToJson<T>(T value)
    {
        return JsonSerializer.Serialize(value, options);
    }

    public static T FromJson<T>(string json)
    {
        var value = JsonSerializer.Deserialize<T>(json, options);

        if (value is null)
        {
            throw new InvalidOperationException(
                $"Could not deserialise {typeof(T).Name} from JSON.");
        }

        return value;
    }
}
