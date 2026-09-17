using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Mpai.Core;

namespace Mpai.Mas.PortData;

// OSD-BVO-V1.5 - Basic Visual Object.
//
// Written against OSD/V1.5/data/BasicVisualObject.json and the Visual Qualifier
// it references, TFA/V1.5/data/VisualQualifier.json.
//
// ENUM NAMES ARE NOT WIRE VALUES. C# cannot spell "ACES2065-1", "BT.2100-PQ",
// "SMPTE 170M" or "3GP", so the enums carry mangled names and ToString() would
// put those mangled names on the wire. Every enum is mapped explicitly here,
// both ways, and an unmapped value is refused rather than guessed at.
//
// FILENAME DOES NOT CROSS. BasicVisualObject carries a FileName; the schema has
// no such property, and rightly - a path on the sending machine means nothing on
// the receiving one. BLIP asks for the bytes first and only falls back to the
// path, so a remote image is answered from its data. Anything that depended on
// the path alone would fail on a remote client, and should.
public sealed class BasicVisualObjectCodec : IPortDataCodec
{
    public string DataType => "OSD-BVO-V1.5";

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition =
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // -- enum <-> wire ---------------------------------------------------------
    private static readonly Dictionary<ColourFormat, string> ColourToWire = new()
    {
        [ColourFormat.ACES2065_1]   = "ACES2065-1",
        [ColourFormat.ACEScg]       = "ACEScg",
        [ColourFormat.BT_601]       = "BT.601",
        [ColourFormat.BT_709]       = "BT.709",
        [ColourFormat.BT_2020]      = "BT.2020",
        [ColourFormat.BT_2100_PQ]   = "BT.2100-PQ",
        [ColourFormat.BT_2100_HLG]  = "BT.2100-HLG",
        [ColourFormat.DCI_P3]       = "DCI-P3",
        [ColourFormat.SMPTE_170M]   = "SMPTE 170M",
        [ColourFormat.SMPTE_240M]   = "SMPTE 240M",
        [ColourFormat.SMPTE_2036_1] = "SMPTE 2036-1",
        [ColourFormat.SMPTE_2084]   = "SMPTE 2084",
        [ColourFormat.SMPTE_2086]   = "SMPTE 2086"
    };

    private static readonly Dictionary<VisualFileFormat, string> FileToWire = new()
    {
        [VisualFileFormat.AVI]     = "AVI",
        [VisualFileFormat.EXIF]    = "EXIF",
        [VisualFileFormat.JPEGXS]  = "JPEGXS",
        [VisualFileFormat.MP4]     = "MP4",
        [VisualFileFormat.MOV]     = "MOV",
        [VisualFileFormat.MKV]     = "MKV",
        [VisualFileFormat.WEBM]    = "WEBM",
        [VisualFileFormat.FLV]     = "FLV",
        [VisualFileFormat.ThreeGP] = "3GP",
        [VisualFileFormat.TIFF]    = "TIFF"
    };

    // Visual2DStaticFormat spells every value as the schema does, so ToString()
    // is correct here - stated rather than assumed, so the next reader knows it
    // was checked and not overlooked.
    private static string StaticToWire(Visual2DStaticFormat f) => f.ToString();

    private static T FromWire<T>(
        string value,
        Dictionary<T, string> map,
        string what) where T : struct
    {
        foreach (var pair in map)
            if (string.Equals(pair.Value, value, StringComparison.Ordinal))
                return pair.Key;

        throw new NotSupportedException($"Unknown {what} on the wire: '{value}'.");
    }

    // -- internal object JSON -> MAS port-data --------------------------------
    public byte[] ToWire(
        string internalJson)
    {
        var visual = MpaiJson.FromJson<BasicVisualObject>(internalJson);

        var wire = new JsonObject
        {
            ["Header"] = "OSD-BVO-V1.5",

            ["BasicVisualObjectID"] =
                string.IsNullOrEmpty(visual.BasicVisualObjectID)
                    ? Guid.NewGuid().ToString()
                    : visual.BasicVisualObjectID,

            ["BasicVisualObjectData"] = new JsonArray(
                new JsonObject
                {
                    ["Data"] = Convert.ToBase64String(visual.Data)
                })
        };

        if (visual.VisualQualifier is not null)
            wire["VisualQualifier"] = QualifierToWire(visual.VisualQualifier);

        return Encoding.UTF8.GetBytes(wire.ToJsonString(WireOptions));
    }

    // -- MAS port-data -> internal object JSON --------------------------------
    public string ToInternal(
        byte[] wire)
    {
        var root = JsonNode.Parse(Encoding.UTF8.GetString(wire)) as JsonObject
                   ?? throw new FormatException(
                          "Basic Visual Object port-data is not a JSON object.");

        var visual = new BasicVisualObject
        {
            BasicVisualObjectID = (string?)root["BasicVisualObjectID"] ?? string.Empty,
            Data                = InlineData(root),
            VisualQualifier     = QualifierFromWire(root["VisualQualifier"] as JsonObject)
        };

        return MpaiJson.ToJson(visual);
    }

    private static byte[] InlineData(
        JsonObject root)
    {
        if (root["BasicVisualObjectData"] is not JsonArray entries)
            return Array.Empty<byte>();

        foreach (var entry in entries)
        {
            if (entry is not JsonObject o) continue;

            if (o["Data"] is not null)
                return Convert.FromBase64String((string)o["Data"]!);

            if (o["DataURI"] is not null || o["ID"] is not null)
                throw new NotSupportedException(
                    "Basic Visual Object carries its data by reference " +
                    "(DataURI or ID); this build reads only the inline form.");
        }

        return Array.Empty<byte>();
    }

    // -- the Visual Qualifier -------------------------------------------------
    private static JsonObject QualifierToWire(
        VisualQualifier q)
    {
        var wire = new JsonObject
        {
            ["Header"]            = "TFA-VIQ-V1.5",
            ["VisualQualifierID"] = string.IsNullOrEmpty(q.VisualQualifierID)
                                        ? Guid.NewGuid().ToString()
                                        : q.VisualQualifierID,
            ["SubTypes"]          = SubTypesToWire(q.SubType),
            ["Formats"]           = FormatsToWire(q.Format)
        };

        if (!string.IsNullOrEmpty(q.MInstanceID))
            wire["MInstanceID"] = q.MInstanceID;

        if (!string.IsNullOrEmpty(q.UEnvironmentID))
            wire["UEnvironmentID"] = q.UEnvironmentID;

        if (!string.IsNullOrEmpty(q.DescrMetadata))
            wire["DescrMetadata"] = q.DescrMetadata;

        if (AttributesToWire(q.Attributes) is { } attributes)
            wire["Attributes"] = attributes;

        return wire;
    }

    private static JsonObject SubTypesToWire(
        VisualSubType? s)
    {
        var wire = new JsonObject();
        if (s is null) return wire;

        if (s.ColourFormat is { } colour)
            wire["ColourFormat"] = ColourToWire[colour];

        if (s.AlphaChannel is { } alpha)          wire["AlphaChannel"] = alpha;
        if (s.Brightness is { } brightness)       wire["Brightness"]   = brightness;
        if (!string.IsNullOrEmpty(s.ColourSubsampling))
            wire["ColourSubsampling"] = s.ColourSubsampling;
        if (!string.IsNullOrEmpty(s.YUV))  wire["YUV"]  = s.YUV;
        if (!string.IsNullOrEmpty(s.CMYK)) wire["CMYK"] = s.CMYK;

        return wire;
    }

    private static JsonObject FormatsToWire(
        VisualFormat? format)
    {
        var wire = new JsonObject();
        if (format is null) return wire;

        var content = new JsonObject();

        if (format.Content?.TimeSampling is { } ts)
        {
            var sampling = new JsonObject();

            // Precision is an ARRAY of { "bits-per-pixel": n }. This is the
            // visual side saying explicitly what Precision means, which is why
            // PCM's Precision carries the bits per sample and not
            // SamplePrecision.
            if (ts.Precision is { Length: > 0 })
            {
                var precision = new JsonArray();
                foreach (var bpp in ts.Precision)
                    precision.Add(new JsonObject
                    {
                        ["bits-per-pixel"] = bpp.BitsPerPixelValue
                    });
                sampling["Precision"] = precision;
            }

            if (ts.Time is { } t)  sampling["Time"]  = t;
            if (ts.Space is { } sp) sampling["Space"] = sp;

            if (sampling.Count > 0) content["TimeSampling"] = sampling;
        }

        if (format.Content?.TwoD?.Static is { } still)
            content["2D"] = new JsonObject
            {
                ["Static"] = StaticToWire(still)
            };

        if (content.Count > 0) wire["Content"] = content;

        if (format.Transport?.FileFormat is { } file)
            wire["Transport"] = new JsonObject
            {
                ["FileFormat"] = FileToWire[file]
            };

        return wire;
    }

    private static JsonObject? AttributesToWire(
        VisualAttributes? a)
    {
        if (a is null) return null;

        var wire = new JsonObject();

        if (a.Source is { } source)
        {
            var s = new JsonObject();
            if (!string.IsNullOrEmpty(source.Real))      s["Real"]      = source.Real;
            if (!string.IsNullOrEmpty(source.Synthetic)) s["Synthetic"] = source.Synthetic;
            if (s.Count > 0) wire["Source"] = s;
        }

        if (!string.IsNullOrEmpty(a.Metadata))
            wire["Metadata"] = a.Metadata;

        // "Face" | "Body" | "Object", written by CVE-VSI.
        if (!string.IsNullOrEmpty(a.VisualObjectType))
            wire["VisualObjectType"] = a.VisualObjectType;

        // NOT CARRIED, and worth knowing precisely because CVE-VSI sets these too:
        //   Attributes.ObjectID             -> OSD InstanceIdentifier
        //   Attributes.EntityInternalStatus -> MMC PersonalStatus
        // Each is a referenced schema wanting its own codec, shared with every
        // Object that carries it.

        if (DeviceToWire(a.Device) is { } device)
            wire["Device"] = device;

        return wire.Count > 0 ? wire : null;
    }

    private static JsonObject? DeviceToWire(
        VisualDevice? d)
    {
        if (d is null) return null;

        var wire = new JsonObject();

        if (!string.IsNullOrEmpty(d.DeviceID))     wire["DeviceID"]     = d.DeviceID;
        if (!string.IsNullOrEmpty(d.DeviceRole))   wire["DeviceRole"]   = d.DeviceRole;
        if (!string.IsNullOrEmpty(d.DeviceType))   wire["DeviceType"]   = d.DeviceType;
        if (!string.IsNullOrEmpty(d.Manufacturer)) wire["Manufacturer"] = d.Manufacturer;
        if (!string.IsNullOrEmpty(d.Model))        wire["Model"]        = d.Model;

        if (d.Optics is { } o)
        {
            var optics = new JsonObject();
            if (o.FieldOfView is { } fov) optics["FieldOfView"] = fov;
            if (o.FocalLength is { } fl)  optics["FocalLength"] = fl;
            if (o.Aperture is { } ap)     optics["Aperture"]    = ap;
            if (optics.Count > 0) wire["Optics"] = optics;
        }

        if (d.CaptureConfiguration is { } cc)
        {
            var c = new JsonObject();
            if (!string.IsNullOrEmpty(cc.Resolution))   c["Resolution"]   = cc.Resolution;
            if (cc.FrameRate is { } fr)                 c["FrameRate"]    = fr;
            if (!string.IsNullOrEmpty(cc.SamplingMode)) c["SamplingMode"] = cc.SamplingMode;
            if (c.Count > 0) wire["CaptureConfiguration"] = c;
        }

        if (d.RenderConfiguration is { } rc)
        {
            var r = new JsonObject();
            if (!string.IsNullOrEmpty(rc.Resolution))  r["Resolution"]  = rc.Resolution;
            if (!string.IsNullOrEmpty(rc.DisplayType)) r["DisplayType"] = rc.DisplayType;
            if (rc.RefreshRate is { } rr)              r["RefreshRate"] = rr;
            if (r.Count > 0) wire["RenderConfiguration"] = r;
        }

        if (d.OperationalParameters is { } op)
        {
            var p = new JsonObject();
            if (op.Brightness is { } b)   p["Brightness"]   = b;
            if (op.Contrast is { } co)    p["Contrast"]     = co;
            if (op.DynamicRange is { } dr) p["DynamicRange"] = dr;
            if (p.Count > 0) wire["OperationalParameters"] = p;
        }

        return wire.Count > 0 ? wire : null;
    }

    private static VisualQualifier? QualifierFromWire(
        JsonObject? wire)
    {
        if (wire is null) return null;

        return new VisualQualifier
        {
            MInstanceID       = (string?)wire["MInstanceID"],
            UEnvironmentID    = (string?)wire["UEnvironmentID"],
            VisualQualifierID = (string?)wire["VisualQualifierID"] ?? string.Empty,
            DescrMetadata     = (string?)wire["DescrMetadata"],
            SubType           = SubTypesFromWire(wire["SubTypes"] as JsonObject),
            Format            = FormatsFromWire(wire["Formats"] as JsonObject),
            Attributes        = AttributesFromWire(wire["Attributes"] as JsonObject)
        };
    }

    private static VisualSubType? SubTypesFromWire(
        JsonObject? wire)
    {
        if (wire is null || wire.Count == 0) return null;

        return new VisualSubType
        {
            ColourFormat = wire["ColourFormat"] is { } c
                ? FromWire((string)c!, ColourToWire, "ColourFormat")
                : null,
            AlphaChannel      = (double?)wire["AlphaChannel"],
            Brightness        = (double?)wire["Brightness"],
            ColourSubsampling = (string?)wire["ColourSubsampling"],
            YUV               = (string?)wire["YUV"],
            CMYK              = (string?)wire["CMYK"]
        };
    }

    private static VisualFormat? FormatsFromWire(
        JsonObject? wire)
    {
        if (wire is null || wire.Count == 0) return null;

        VisualTimeSampling? sampling = null;
        if (wire["Content"]?["TimeSampling"] is JsonObject ts)
        {
            BitsPerPixel[]? precision = null;
            if (ts["Precision"] is JsonArray entries)
            {
                var list = new List<BitsPerPixel>();
                foreach (var entry in entries)
                    if (entry is JsonObject o && o["bits-per-pixel"] is { } bpp)
                        list.Add(new BitsPerPixel { BitsPerPixelValue = (int)bpp! });
                if (list.Count > 0) precision = list.ToArray();
            }

            sampling = new VisualTimeSampling
            {
                Precision = precision,
                Time      = (double?)ts["Time"],
                Space     = (double?)ts["Space"]
            };
        }

        Visual2D? twoD = null;
        if (wire["Content"]?["2D"]?["Static"] is { } still)
            twoD = new Visual2D
            {
                Static = Enum.Parse<Visual2DStaticFormat>((string)still!)
            };

        VisualContentFormat? content = null;
        if (sampling is not null || twoD is not null)
            content = new VisualContentFormat { TimeSampling = sampling, TwoD = twoD };

        VisualTransport? transport = null;
        if (wire["Transport"]?["FileFormat"] is { } file)
            transport = new VisualTransport
            {
                FileFormat = FromWire((string)file!, FileToWire, "VisualFileFormat")
            };

        if (content is null && transport is null) return null;

        return new VisualFormat { Content = content, Transport = transport };
    }

    private static VisualAttributes? AttributesFromWire(
        JsonObject? wire)
    {
        if (wire is null || wire.Count == 0) return null;

        VisualSource? source = null;
        if (wire["Source"] is JsonObject s)
            source = new VisualSource
            {
                Real      = (string?)s["Real"],
                Synthetic = (string?)s["Synthetic"]
            };

        return new VisualAttributes
        {
            Source           = source,
            VisualObjectType = (string?)wire["VisualObjectType"],
            Metadata         = (string?)wire["Metadata"],
            Device   = DeviceFromWire(wire["Device"] as JsonObject)
        };
    }

    private static VisualDevice? DeviceFromWire(
        JsonObject? wire)
    {
        if (wire is null) return null;

        return new VisualDevice
        {
            DeviceID     = (string?)wire["DeviceID"],
            DeviceRole   = (string?)wire["DeviceRole"],
            DeviceType   = (string?)wire["DeviceType"],
            Manufacturer = (string?)wire["Manufacturer"],
            Model        = (string?)wire["Model"],

            Optics = wire["Optics"] is JsonObject o
                ? new VisualOptics
                  {
                      FieldOfView = (double?)o["FieldOfView"],
                      FocalLength = (double?)o["FocalLength"],
                      Aperture    = (double?)o["Aperture"]
                  }
                : null,

            CaptureConfiguration = wire["CaptureConfiguration"] is JsonObject cc
                ? new VisualCaptureConfiguration
                  {
                      Resolution   = (string?)cc["Resolution"],
                      FrameRate    = (double?)cc["FrameRate"],
                      SamplingMode = (string?)cc["SamplingMode"]
                  }
                : null,

            RenderConfiguration = wire["RenderConfiguration"] is JsonObject rc
                ? new VisualRenderConfiguration
                  {
                      Resolution  = (string?)rc["Resolution"],
                      DisplayType = (string?)rc["DisplayType"],
                      RefreshRate = (double?)rc["RefreshRate"]
                  }
                : null,

            OperationalParameters = wire["OperationalParameters"] is JsonObject op
                ? new VisualOperationalParameters
                  {
                      Brightness   = (double?)op["Brightness"],
                      Contrast     = (double?)op["Contrast"],
                      DynamicRange = (double?)op["DynamicRange"]
                  }
                : null
        };
    }
}