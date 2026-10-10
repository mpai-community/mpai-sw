using System;
using System.Text;
using System.Text.Json.Nodes;

namespace Mpai.Aif.PortData;

// XRV-SAV-V1.0 - Speaking Avatar: what Response and Scene Rendering gives a client to play: the Avatar animated by its Face and Body Descriptors, with its Speech, ready to play.
//
// It crosses MPAI-MAS in the form its schema gives it, which is the form the C#
// classes write (checked against the schema by AvatarSchemaTests): carried as it
// is, its Header checked. Without this codec a Remote Client Application could not
// give or receive it, and every Module that renders with RSR was out of reach over
// MPAI-MAS (found testing the Linux update, 2026/10/04).
public sealed class SpeakingAvatarCodec : IPortDataCodec
{
    public string DataType => "XRV-SAV-V1.0";

    public byte[] ToWire(string internalJson) =>
        Encoding.UTF8.GetBytes(Checked(internalJson).ToJsonString());

    public string ToInternal(byte[] wire) =>
        Checked(Encoding.UTF8.GetString(wire)).ToJsonString();

    private static JsonObject Checked(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject
                   ?? throw new FormatException("Speaking Avatar is not a JSON object.");
        var header = (string?)root["Header"];
        if (header is not null && header != "XRV-SAV-V1.0")
            throw new FormatException($"Speaking Avatar carries the Header '{header}'.");
        root["Header"] = "XRV-SAV-V1.0";
        return root;
    }
}
