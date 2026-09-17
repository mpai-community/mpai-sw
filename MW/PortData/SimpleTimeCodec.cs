using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Mas.PortData;

// OSD-STM-V1.5 - Simple Time.
//
// A DATA TYPE IN ITS OWN RIGHT, AND A REFERENCED ONE. Simple Time is carried on
// MMC-MAD's InputSpeechTime Port, and it is also embedded in every frame of a
// Face Descriptors Object. The conversion therefore lives in SimpleTimeWire,
// which both this serialiser and the FDO serialiser call - one definition, so
// the two cannot drift.
public static class SimpleTimeWire
{
    public static JsonObject ToWire(
        SimpleTime time)
    {
        var segments = new JsonArray();
        foreach (var s in time.SimpleTimeData)
        {
            var segment = new JsonObject
            {
                ["FlagsByte"] = s.FlagsByte,
                ["StartTime"] = s.StartTime,
                ["EndTime"]   = s.EndTime
            };

            if (!string.IsNullOrEmpty(s.AccuracyMode))
                segment["AccuracyMode"] = s.AccuracyMode;

            if (s.AccuracyPlusMinus is { } pm)       segment["AccuracyPlusMinus"] = pm;
            if (s.AccuracyStartPlusMinus is { } spm) segment["AccuracyStartPlusMinus"] = spm;
            if (s.AccuracyEndPlusMinus is { } epm)   segment["AccuracyEndPlusMinus"] = epm;

            if (s.TimeType is { } tt)              segment["TimeType"] = tt;
            if (!string.IsNullOrEmpty(s.TimeUnit)) segment["TimeUnit"] = s.TimeUnit;
            if (s.Reserved is { } r)               segment["Reserved"] = r;

            segments.Add(segment);
        }

        var wire = new JsonObject
        {
            ["Header"]       = "OSD-STM-V1.5",
            ["SimpleTimeID"] = string.IsNullOrEmpty(time.SimpleTimeID)
                                   ? Guid.NewGuid().ToString()
                                   : time.SimpleTimeID,
            ["SimpleTimeData"] = segments
        };

        if (!string.IsNullOrEmpty(time.MInstanceID))
            wire["MInstanceID"] = time.MInstanceID;

        if (!string.IsNullOrEmpty(time.UEnvironmentID))
            wire["UEnvironmentID"] = time.UEnvironmentID;

        if (!string.IsNullOrEmpty(time.DescrMetadata))
            wire["DescrMetadata"] = time.DescrMetadata;

        return wire;
    }

    public static SimpleTime? FromWire(
        JsonObject? wire)
    {
        if (wire is null) return null;

        var segments = new List<TimeSegment>();
        if (wire["SimpleTimeData"] is JsonArray entries)
            foreach (var entry in entries)
            {
                if (entry is not JsonObject s) continue;

                segments.Add(new TimeSegment
                {
                    FlagsByte              = (int?)s["FlagsByte"] ?? 0,
                    StartTime              = (double?)s["StartTime"] ?? 0d,
                    EndTime                = (double?)s["EndTime"] ?? 0d,
                    AccuracyMode           = (string?)s["AccuracyMode"] ?? "single",
                    AccuracyPlusMinus      = (double?)s["AccuracyPlusMinus"],
                    AccuracyStartPlusMinus = (double?)s["AccuracyStartPlusMinus"],
                    AccuracyEndPlusMinus   = (double?)s["AccuracyEndPlusMinus"],
                    TimeType               = (bool?)s["TimeType"],
                    TimeUnit               = (string?)s["TimeUnit"],
                    Reserved               = (int?)s["Reserved"]
                });
            }

        return new SimpleTime
        {
            MInstanceID    = (string?)wire["MInstanceID"],
            UEnvironmentID = (string?)wire["UEnvironmentID"],
            SimpleTimeID   = (string?)wire["SimpleTimeID"] ?? string.Empty,
            DescrMetadata  = (string?)wire["DescrMetadata"],
            SimpleTimeData = segments
        };
    }
}

// The Port-data serialiser for a Simple Time crossing a Port on its own.
public sealed class SimpleTimeCodec : IPortDataCodec
{
    public string DataType => "OSD-STM-V1.5";

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition =
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public byte[] ToWire(
        string internalJson)
    {
        var time = MpaiJson.FromJson<SimpleTime>(internalJson);
        return Encoding.UTF8.GetBytes(
            SimpleTimeWire.ToWire(time).ToJsonString(WireOptions));
    }

    public string ToInternal(
        byte[] wire)
    {
        var root = JsonNode.Parse(Encoding.UTF8.GetString(wire)) as JsonObject
                   ?? throw new FormatException(
                          "Simple Time port-data is not a JSON object.");

        return MpaiJson.ToJson(SimpleTimeWire.FromWire(root));
    }
}