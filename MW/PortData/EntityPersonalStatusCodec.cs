using System;
using System.Text;
using System.Text.Json.Nodes;

namespace Mpai.Mas.PortData;

// MMC-EPS-V2.5 - Entity Personal Status: the Cognitive State, Emotion and Social
// Attitude of the person or the machine, split by modality (Text, Speech, Face,
// Gesture).
//
// It crosses MPAI-MAS because EDP's Personal Status Ports do: MPD sends the
// person's Personal Status in, and reads the machine's Personal Status back, each
// turn, over whichever machine runs EDP - local or remote (MPAI-MAS: a Sub-AIM's
// Relation other than Internal).
//
// Inside and on the wire the Entity Personal Status has the same form, the one
// EDP uses: { "Header": "MMC-EPS-V2.5", ... the per-modality Personal Status
// objects ... }. schemas/MMC/V2.5/data/EntityPersonalStatus.json is not in the
// repository yet; when it is, PortDataSchema checks the wire form against it and
// this can follow it - the same note SummaryCodec carries.
public sealed class EntityPersonalStatusCodec : IPortDataCodec
{
    public string DataType => "MMC-EPS-V2.5";

    public byte[] ToWire(string internalJson) =>
        Encoding.UTF8.GetBytes(Checked(internalJson).ToJsonString());

    public string ToInternal(byte[] wire) =>
        Checked(Encoding.UTF8.GetString(wire)).ToJsonString();

    private static JsonObject Checked(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject
                   ?? throw new FormatException("Entity Personal Status is not a JSON object.");
        var header = (string?)root["Header"];
        if (header is not null && header != "MMC-EPS-V2.5")
            throw new FormatException($"Entity Personal Status carries the Header '{header}'.");
        root["Header"] = "MMC-EPS-V2.5";
        return root;
    }
}
