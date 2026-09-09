using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mpai.Core;

// BasicTextData is a "oneOf" in the schema:
//     { Data }                  inline data
//     { Length, DataURI }       reference to stored data
//     { ID }                    reference by identifier
//
// System.Text.Json cannot deserialise the abstract BasicTextDataItem on its
// own, so this converter selects the branch the way the schema does: by the
// properties present.
public sealed class BasicTextDataConverter
    : JsonConverter<BasicTextDataItem>
{
    public override BasicTextDataItem Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document =
            JsonDocument.ParseValue(ref reader);

        var element = document.RootElement;

        if (element.TryGetProperty("Data", out var data))
        {
            return new InlineTextData(
                data.GetString() ?? "");
        }

        if (element.TryGetProperty("DataURI", out var uri))
        {
            var length =
                element.TryGetProperty("Length", out var len)
                    ? len.GetInt64()
                    : 0L;

            return new ReferencedData(
                length,
                uri.GetString() ?? "");
        }

        if (element.TryGetProperty("ID", out var id))
        {
            return new IdentifiedData(
                id.GetString() ?? "");
        }

        throw new JsonException(
            "BasicTextData item matches none of Data | Length+DataURI | ID.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        BasicTextDataItem value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        switch (value)
        {
            case InlineTextData inline:
                writer.WriteString("Data", inline.Data);
                break;

            case ReferencedData referenced:
                writer.WriteNumber("Length", referenced.Length);
                writer.WriteString("DataURI", referenced.DataURI);
                break;

            case IdentifiedData identified:
                writer.WriteString("ID", identified.ID);
                break;

            default:
                throw new JsonException(
                    $"Unknown BasicTextData item: {value.GetType().Name}");
        }

        writer.WriteEndObject();
    }
}
