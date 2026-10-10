using System;
using System.Text;
using System.Text.Json.Nodes;

namespace Mpai.Aif.PortData;

// PAF-AVT-V1.6 - Avatar: the Model a reply animates - offered by a client with each utterance, since the client holds the model and its page draws it.
//
// It crosses MPAI-MAS in the form its schema gives it, which is the form the C#
// classes write (checked against the schema by AvatarSchemaTests): carried as it
// is, its Header checked. Without this codec a Remote Client Application could not
// give or receive it, and every Module that renders with RSR was out of reach over
// MPAI-MAS (found testing the Linux update, 2026/10/04).
public sealed class AvatarCodec : IPortDataCodec
{
    public string DataType => "PAF-AVT-V1.6";

    public byte[] ToWire(string internalJson) =>
        Encoding.UTF8.GetBytes(Checked(internalJson).ToJsonString());

    public string ToInternal(byte[] wire) =>
        Checked(Encoding.UTF8.GetString(wire)).ToJsonString();

    private static JsonObject Checked(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject
                   ?? throw new FormatException("Avatar is not a JSON object.");
        var header = (string?)root["Header"];
        if (header is not null && header != "PAF-AVT-V1.6")
            throw new FormatException($"Avatar carries the Header '{header}'.");
        root["Header"] = "PAF-AVT-V1.6";
        return root;
    }
}
