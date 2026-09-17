using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Mas.PortData;

// PAF-FDO-V1.6 - Face Descriptors Object.
//
// AN ANIMATION TIMELINE, NOT ONE POSE. FaceDescriptorsData is an array of
// frames, each carrying a Time and a block of descriptors in the format the
// Qualifier names. PAF-GFD produces one frame per animation step across the
// whole utterance. Drop the Times and what remains is a pile of poses with no
// ordering - which is why an avatar given descriptors without them renders
// nothing at all.
//
// SIMPLETIME IS CARRIED IN FULL, segments and flags, because it is a referenced
// Data Type in its own right and several Objects carry it.
public sealed class FaceDescriptorsObjectCodec : IPortDataCodec
{
    public string DataType => "PAF-FDO-V1.6";

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition =
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public byte[] ToWire(
        string internalJson)
    {
        var fdo = MpaiJson.FromJson<FaceDescriptorsObject>(internalJson);

        var frames = new JsonArray();
        foreach (var frame in fdo.FaceDescriptorsData)
        {
            var item = new JsonObject();

            if (frame.Time is not null)
                item["Time"] = SimpleTimeWire.ToWire(frame.Time);

            // Exactly one of the three variants: additionalProperties is false
            // on each, so an item must not carry Data AND DataURI.
            if (frame.Data is not null)
            {
                item["Data"] = frame.Data;
            }
            else if (frame.DataURI is not null)
            {
                if (frame.DataLength is { } length) item["DataLength"] = length;
                item["DataURI"] = frame.DataURI;
            }
            else if (frame.DataID is not null)
            {
                item["ID"] = frame.DataID;
            }

            frames.Add(item);
        }

        var wire = new JsonObject
        {
            ["Header"] = "PAF-FDO-V1.6",

            ["FaceDescriptorsObjectID"] =
                string.IsNullOrEmpty(fdo.FaceDescriptorsObjectID)
                    ? Guid.NewGuid().ToString()
                    : fdo.FaceDescriptorsObjectID,

            ["FaceDescriptorsData"] = frames
        };

        if (!string.IsNullOrEmpty(fdo.MInstanceID))
            wire["MInstanceID"] = fdo.MInstanceID;

        if (!string.IsNullOrEmpty(fdo.UEnvironmentID))
            wire["UEnvironmentID"] = fdo.UEnvironmentID;

        if (!string.IsNullOrEmpty(fdo.DescrMetadata))
            wire["DescrMetadata"] = fdo.DescrMetadata;

        if (fdo.FaceDescriptorsObjectTime is not null)
            wire["FaceDescriptorsObjectTime"] =
                SimpleTimeWire.ToWire(fdo.FaceDescriptorsObjectTime);

        if (fdo.FaceDescriptorsQualifier is not null)
            wire["FaceDescriptorsQualifier"] =
                QualifierToWire(fdo.FaceDescriptorsQualifier);

        return Encoding.UTF8.GetBytes(wire.ToJsonString(WireOptions));
    }

    public string ToInternal(
        byte[] wire)
    {
        var root = JsonNode.Parse(Encoding.UTF8.GetString(wire)) as JsonObject
                   ?? throw new FormatException(
                          "Face Descriptors Object port-data is not a JSON object.");

        var frames = new List<FaceDescriptorsDataItem>();
        if (root["FaceDescriptorsData"] is JsonArray entries)
            foreach (var entry in entries)
            {
                if (entry is not JsonObject o) continue;

                frames.Add(new FaceDescriptorsDataItem
                {
                    Time       = SimpleTimeWire.FromWire(o["Time"] as JsonObject),
                    Data       = (string?)o["Data"],
                    DataURI    = (string?)o["DataURI"],
                    DataLength = (long?)o["DataLength"],
                    DataID     = (string?)o["ID"]
                });
            }

        var fdo = new FaceDescriptorsObject
        {
            MInstanceID               = (string?)root["MInstanceID"],
            UEnvironmentID            = (string?)root["UEnvironmentID"],
            FaceDescriptorsObjectID   = (string?)root["FaceDescriptorsObjectID"] ?? string.Empty,
            DescrMetadata             = (string?)root["DescrMetadata"],
            FaceDescriptorsObjectTime = SimpleTimeWire.FromWire(
                                            root["FaceDescriptorsObjectTime"] as JsonObject),
            FaceDescriptorsData       = frames,
            FaceDescriptorsQualifier  = QualifierFromWire(
                                            root["FaceDescriptorsQualifier"] as JsonObject)
        };

        return MpaiJson.ToJson(fdo);
    }
    // -- the Qualifier --------------------------------------------------------
    private static JsonObject QualifierToWire(
        FaceDescriptorsQualifier q)
    {
        var formats = new JsonObject();

        // Which descriptor format the Data is in - "FACS-AU" for the visemes and
        // expression an avatar renders. Without it the far end has numbers and
        // no statement of what they describe.
        if (!string.IsNullOrEmpty(q.Formats?.ContentFormat))
            formats["ContentFormat"] = q.Formats!.ContentFormat;

        return new JsonObject
        {
            ["Header"] = "TFA-FDQ-V1.5",

            ["FaceDescriptorsQualifierID"] =
                string.IsNullOrEmpty(q.FaceDescriptorsQualifierID)
                    ? Guid.NewGuid().ToString()
                    : q.FaceDescriptorsQualifierID,

            // Required by the schema and defined there with no properties at
            // all, so always empty.
            ["SubTypes"] = new JsonObject(),
            ["Formats"]  = formats
        };
    }

    private static FaceDescriptorsQualifier? QualifierFromWire(
        JsonObject? wire)
    {
        if (wire is null) return null;

        return new FaceDescriptorsQualifier
        {
            FaceDescriptorsQualifierID =
                (string?)wire["FaceDescriptorsQualifierID"] ?? string.Empty,
            Formats = new FaceDescriptorsFormats
            {
                ContentFormat = (string?)wire["Formats"]?["ContentFormat"]
            }
        };
    }

}
