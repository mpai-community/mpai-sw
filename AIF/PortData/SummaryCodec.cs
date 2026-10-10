using System;
using System.Text;
using System.Text.Json.Nodes;

namespace Mpai.Aif.PortData;

// MMC-SUM-V2.5 - Summary, the running memory of a dialogue.
//
// It crosses MPAI-MAS because the memory is the conversation's, not the
// Service's: a workflow receives it from EDP after one turn and offers it back
// with the next, so nothing a person says stays on the Service between turns.
//
// Inside and on the wire the Summary has the same form, the one EDP uses:
// { "Header": "MMC-SUM-V2.5", "SummaryID": ..., "SummaryData": [ { "Data": "..." } ] }.
// schemas/MMC/V2.5/data/Summary.json is not in the repository yet; when it is,
// PortDataSchema checks the wire form against it and this can follow it.
public sealed class SummaryCodec : IPortDataCodec
{
    public string DataType => "MMC-SUM-V2.5";

    public byte[] ToWire(string internalJson) =>
        Encoding.UTF8.GetBytes(Checked(internalJson).ToJsonString());

    public string ToInternal(byte[] wire) =>
        Checked(Encoding.UTF8.GetString(wire)).ToJsonString();

    private static JsonObject Checked(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject
                   ?? throw new FormatException("Summary is not a JSON object.");
        var header = (string?)root["Header"];
        if (header is not null && header != "MMC-SUM-V2.5")
            throw new FormatException($"Summary carries the Header '{header}'.");
        root["Header"] = "MMC-SUM-V2.5";
        return root;
    }
}
