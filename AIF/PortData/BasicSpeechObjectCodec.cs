using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Mpai.Core;

namespace Mpai.Aif.PortData;

// OSD-BSO-V1.5 - Basic Speech Object.
//
// Written against OSD/V1.5/data/BasicSpeechObject.json and the Speech Qualifier
// it references, TFA/V1.5/data/SpeechQualifier.json.
//
// THE QUALIFIER IS HALF THE OBJECT. An Object is data plus a Qualifier, and the
// Qualifier is what says the data is speech, in which language, at what sampling
// frequency. The previous translator wrote the qualifier under its C# property
// name ("SpeechQualifier") where the schema says "BasicSpeechDataQualifier", and
// read none of it back - so the far end received bytes and no statement of what
// they meant. The server then re-attached a Speech Qualifier by hand, because
// otherwise every remote utterance was recognised in whatever language the
// server had been configured with. Carrying the qualifier properly removes the
// reason for that patch.
//
// NAMES DIFFER BETWEEN THE WIRE AND THE CODE, DELIBERATELY. The schema says
// Formats and SubTypes; the C# says Format and SubType, and six applications use
// those types as they stand. A codec exists precisely so a wire format and an
// implementation can each keep their own names. The mapping is here, in one
// file, rather than spread as a rename across six applications.
public sealed class BasicSpeechObjectCodec : IPortDataCodec
{
    public string DataType => "OSD-BSO-V1.5";

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition =
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // -- internal object JSON -> MAS port-data --------------------------------
    public byte[] ToWire(
        string internalJson)
    {
        var speech = MpaiJson.FromJson<BasicSpeechObject>(internalJson);

        var wire = new JsonObject
        {
            // The schema pins this as a const. It is not the sender's to choose:
            // a Basic Speech Object that announced itself as OSD-SPO-V1.5 would
            // be read as a Speech Object by anyone who trusted the Header.
            ["Header"] = "OSD-BSO-V1.5",

            ["BasicSpeechObjectID"] =
                string.IsNullOrEmpty(speech.BasicSpeechObjectID)
                    ? Guid.NewGuid().ToString()
                    : speech.BasicSpeechObjectID,

            // An ARRAY of entries, each one of three variants. The inline
            // { "Data": base64 } form is used here. The schema's other two -
            // { DataLength, DataURI } and { ID } - are how the standard carries
            // payloads too large to inline, which is worth returning to when
            // this runs over a real link rather than loopback.
            ["BasicSpeechObjectData"] = new JsonArray(
                new JsonObject
                {
                    ["Data"] = Convert.ToBase64String(speech.Data)
                })
        };

        if (!string.IsNullOrEmpty(speech.MInstanceID))
            wire["MInstanceID"] = speech.MInstanceID;

        if (!string.IsNullOrEmpty(speech.UEnvironmentID))
            wire["UEnvironmentID"] = speech.UEnvironmentID;

        if (!string.IsNullOrEmpty(speech.DescrMetadata))
            wire["DescrMetadata"] = speech.DescrMetadata;

        if (speech.SpeechQualifier is not null)
            wire["BasicSpeechDataQualifier"] =
                QualifierToWire(speech.SpeechQualifier);

        return Encoding.UTF8.GetBytes(wire.ToJsonString(WireOptions));
    }

    // -- MAS port-data -> internal object JSON --------------------------------
    public string ToInternal(
        byte[] wire)
    {
        var root = JsonNode.Parse(Encoding.UTF8.GetString(wire)) as JsonObject
                   ?? throw new FormatException(
                          "Basic Speech Object port-data is not a JSON object.");

        var data      = InlineData(root);
        var qualifier = QualifierFromWire(root["BasicSpeechDataQualifier"] as JsonObject);

        var speech = new BasicSpeechObject
        {
            MInstanceID         = (string?)root["MInstanceID"],
            UEnvironmentID      = (string?)root["UEnvironmentID"],
            BasicSpeechObjectID = (string?)root["BasicSpeechObjectID"] ?? string.Empty,
            DescrMetadata       = (string?)root["DescrMetadata"],
            Data                = data,
            SpeechQualifier     = qualifier
        };

        return MpaiJson.ToJson(speech);
    }

    // The first entry carrying inline Data. The reference variants are not
    // resolved: rather than return empty bytes and let an AIM invent something
    // from silence, say plainly that this build cannot fetch them.
    private static byte[] InlineData(
        JsonObject root)
    {
        if (root["BasicSpeechObjectData"] is not JsonArray entries)
            return Array.Empty<byte>();

        foreach (var entry in entries)
        {
            if (entry is not JsonObject o) continue;

            if (o["Data"] is not null)
                return Convert.FromBase64String((string)o["Data"]!);

            if (PayloadReferences.TryResolve((string?)o["DataURI"]) is { } resolved)
                return resolved;

            if (o["DataURI"] is not null || o["ID"] is not null)
                throw new NotSupportedException(
                    "Basic Speech Object carries its data by a reference " +
                    "this Controller did not issue (DataURI or ID); only the inline form and aif:payload references are read.");
        }

        return Array.Empty<byte>();
    }

    // -- the Speech Qualifier -------------------------------------------------
    private static JsonObject QualifierToWire(
        SpeechQualifier q)
    {
        var wire = new JsonObject
        {
            ["Header"]           = "TFA-SPQ-V1.5",
            ["SpeechQualifierID"] = string.IsNullOrEmpty(q.SpeechQualifierID)
                                        ? Guid.NewGuid().ToString()
                                        : q.SpeechQualifierID,

            // Required by the schema, and defined there as an object with no
            // properties and additionalProperties false - so it is always empty.
            // Emitted unconditionally because the schema requires it; the C#
            // SubType is not consulted, because there is nothing it could say.
            ["SubTypes"] = new JsonObject(),

            // Required. "Formats" on the wire, "Format" in the code.
            ["Formats"] = FormatsToWire(q.Format)
        };

        if (!string.IsNullOrEmpty(q.MInstanceID))
            wire["MInstanceID"] = q.MInstanceID;

        if (!string.IsNullOrEmpty(q.UEnvironmentID))
            wire["UEnvironmentID"] = q.UEnvironmentID;

        if (!string.IsNullOrEmpty(q.DescrMetadata))
            wire["DescrMetadata"] = q.DescrMetadata;

        // SpeechQualifierTime is NOT emitted. The schema types it as a
        // SimpleTime; Qualifiers.cs declares it a SpaceTime, and nothing in the
        // tree ever assigns it. Emitting a SpaceTime under that name would put a
        // non-conforming value on the wire to represent something no AIM has
        // said. If an AIM starts setting it, this is where the mismatch appears.

        var attributes = AttributesToWire(q.Attributes);
        if (attributes is not null)
            wire["Attributes"] = attributes;

        return wire;
    }

    private static JsonObject FormatsToWire(
        SpeechFormat? format)
    {
        // Formats is required even when the AIM determined nothing, so an empty
        // object is correct where a missing key would not be.
        var wire = new JsonObject();
        if (format is null) return wire;

        if (format.ContentFormats?.RawData is { } pcm)
        {
            var raw = new JsonObject { ["Header"] = "TFA-PCM-V1.5" };

            if (pcm.SamplingFrequency is { } hz) raw["SamplingFrequency"] = hz;

            // Precision, not SamplePrecision: the number of bits used to
            // quantise a sample, as the visual side states it explicitly.
            // SamplePrecision is in the schema with its meaning no longer known,
            // and is deliberately never written.
            if (pcm.Precision is { } bits) raw["Precision"] = bits;

            wire["ContentFormats"] = new JsonObject { ["RawData"] = raw };
        }

        if (!string.IsNullOrEmpty(format.TransportFormats?.FileFormat))
            wire["TransportFormats"] = new JsonObject
            {
                ["FileFormat"] = format.TransportFormats!.FileFormat
            };

        return wire;
    }

    private static JsonObject? AttributesToWire(
        SpeechAttributes? a)
    {
        if (a is null) return null;

        var wire = new JsonObject();

        if (!string.IsNullOrEmpty(a.Source))
            wire["Source"] = a.Source;

        var metadata = new JsonObject();

        if (a.Metadata?.Language is { } language)
        {
            var l = new JsonObject();
            if (!string.IsNullOrEmpty(language.LanguageCode))
                l["LanguageCode"] = language.LanguageCode;
            if (!string.IsNullOrEmpty(language.LanguageFormat))
                l["LanguageFormat"] = language.LanguageFormat;
            if (l.Count > 0) metadata["Language"] = l;
        }

        if (a.Metadata?.SpeakerProperties is { } sp)
        {
            var p = new JsonObject();
            if (!string.IsNullOrEmpty(sp.SpeakerType)) p["SpeakerType"] = sp.SpeakerType;
            if (sp.SpeakerCount is { } n)              p["SpeakerCount"] = n;
            if (p.Count > 0) metadata["SpeakerProperties"] = p;
        }

        // NOT CARRIED YET, AND SAID OUT LOUD RATHER THAN DROPPED QUIETLY:
        //   Attributes.Metadata.SpeakerIdentity     -> OSD InstanceIdentifier
        //   Attributes.Metadata.ContentDescription  -> OSD TextObject,
        //                                              MMC PersonalStatus
        // Each is a referenced schema in its own right and wants its own codec,
        // shared with every other Object that carries it. Until those exist this
        // codec leaves the fields absent, which the schema permits - but a
        // Speaker Identity determined by MMC-SIR will not reach the far end, and
        // anything relying on it must know that.

        if (metadata.Count > 0) wire["Metadata"] = metadata;

        if (a.SpeechCharacteristics is { } c)
        {
            var sc = new JsonObject();
            if (ValueUnitToWire(c.SpeakingRate) is { } rate)  sc["SpeakingRate"] = rate;
            if (ValueUnitToWire(c.PitchRange)   is { } pitch) sc["PitchRange"]   = pitch;
            if (c.Energy is { } e)
            {
                var energy = new JsonObject();
                if (e.Value is { } v)              energy["Value"] = v;
                if (!string.IsNullOrEmpty(e.Type)) energy["Type"]  = e.Type;
                if (energy.Count > 0) sc["Energy"] = energy;
            }
            if (!string.IsNullOrEmpty(c.Prosody)) sc["Prosody"] = c.Prosody;
            if (c.Disfluencies is { } d)          sc["Disfluencies"] = d;
            if (sc.Count > 0) wire["SpeechCharacteristics"] = sc;
        }

        if (a.Structure is { } st)
        {
            var s = new JsonObject();
            if (st.UtteranceCount is { } u) s["UtteranceCount"] = u;
            if (st.TurnBased is { } t)      s["TurnBased"] = t;
            if (s.Count > 0) wire["Structure"] = s;
        }

        if (DeviceToWire(a.Device) is { } device)
            wire["Device"] = device;

        return wire.Count > 0 ? wire : null;
    }

    private static JsonObject? ValueUnitToWire(
        ValueUnit? vu)
    {
        if (vu is null) return null;
        var o = new JsonObject();
        if (vu.Value is { } v)              o["Value"] = v;
        if (!string.IsNullOrEmpty(vu.Unit)) o["Unit"]  = vu.Unit;
        return o.Count > 0 ? o : null;
    }

    private static JsonObject? DeviceToWire(
        AudioDevice? d)
    {
        if (d is null) return null;

        var wire = new JsonObject();

        if (!string.IsNullOrEmpty(d.DeviceID))     wire["DeviceID"]     = d.DeviceID;
        if (!string.IsNullOrEmpty(d.DeviceRole))   wire["DeviceRole"]   = d.DeviceRole;
        if (!string.IsNullOrEmpty(d.DeviceType))   wire["DeviceType"]   = d.DeviceType;
        if (!string.IsNullOrEmpty(d.Manufacturer)) wire["Manufacturer"] = d.Manufacturer;
        if (!string.IsNullOrEmpty(d.Model))        wire["Model"]        = d.Model;

        // Channel count belongs to the DEVICE, not to a per-channel record in
        // the PCM block.
        if (d.CaptureConfiguration is { } cc)
        {
            var c = new JsonObject();
            if (cc.ChannelCount is { } n)               c["ChannelCount"] = n;
            if (!string.IsNullOrEmpty(cc.SamplingMode)) c["SamplingMode"] = cc.SamplingMode;
            if (c.Count > 0) wire["CaptureConfiguration"] = c;
        }

        if (d.RenderConfiguration is { } rc)
        {
            var r = new JsonObject();
            if (rc.ChannelCount is { } n)                r["ChannelCount"]  = n;
            if (!string.IsNullOrEmpty(rc.RenderingMode)) r["RenderingMode"] = rc.RenderingMode;
            if (r.Count > 0) wire["RenderConfiguration"] = r;
        }

        if (d.Synchronisation is { } sy)
        {
            var s = new JsonObject();
            if (!string.IsNullOrEmpty(sy.ClockType)) s["ClockType"] = sy.ClockType;
            if (!string.IsNullOrEmpty(sy.Reference)) s["Reference"] = sy.Reference;
            if (s.Count > 0) wire["Synchronisation"] = s;
        }

        return wire.Count > 0 ? wire : null;
    }

    private static SpeechQualifier? QualifierFromWire(
        JsonObject? wire)
    {
        if (wire is null) return null;

        var formats    = wire["Formats"]    as JsonObject;
        var attributes = wire["Attributes"] as JsonObject;

        return new SpeechQualifier
        {
            MInstanceID       = (string?)wire["MInstanceID"],
            UEnvironmentID    = (string?)wire["UEnvironmentID"],
            SpeechQualifierID = (string?)wire["SpeechQualifierID"] ?? string.Empty,
            DescrMetadata     = (string?)wire["DescrMetadata"],
            Format            = FormatsFromWire(formats),
            Attributes        = AttributesFromWire(attributes)
        };
    }

    private static SpeechFormat? FormatsFromWire(
        JsonObject? wire)
    {
        if (wire is null) return null;

        SpeechContentFormats? content = null;
        if (wire["ContentFormats"]?["RawData"] is JsonObject raw)
            content = new SpeechContentFormats
            {
                RawData = new Pcm
                {
                    SamplingFrequency = (double?)raw["SamplingFrequency"],
                    Precision         = (int?)raw["Precision"]
                    // SamplePrecision is not read back: nothing writes it.
                }
            };

        SpeechTransportFormats? transport = null;
        if (wire["TransportFormats"]?["FileFormat"] is { } file)
            transport = new SpeechTransportFormats
            {
                FileFormat = (string?)file
            };

        if (content is null && transport is null) return null;

        return new SpeechFormat
        {
            ContentFormats   = content,
            TransportFormats = transport
        };
    }

    private static SpeechAttributes? AttributesFromWire(
        JsonObject? wire)
    {
        if (wire is null) return null;

        SpeechMetadata? metadata = null;
        if (wire["Metadata"] is JsonObject m)
        {
            Language? language = null;
            if (m["Language"] is JsonObject l)
                language = new Language
                {
                    LanguageCode   = (string?)l["LanguageCode"],
                    LanguageFormat = (string?)l["LanguageFormat"]
                };

            SpeakerProperties? speaker = null;
            if (m["SpeakerProperties"] is JsonObject s)
                speaker = new SpeakerProperties
                {
                    SpeakerType  = (string?)s["SpeakerType"],
                    SpeakerCount = (int?)s["SpeakerCount"]
                };

            if (language is not null || speaker is not null)
                metadata = new SpeechMetadata
                {
                    Language          = language,
                    SpeakerProperties = speaker
                };
        }

        SpeechCharacteristics? characteristics = null;
        if (wire["SpeechCharacteristics"] is JsonObject c)
            characteristics = new SpeechCharacteristics
            {
                SpeakingRate = ValueUnitFromWire(c["SpeakingRate"] as JsonObject),
                PitchRange   = ValueUnitFromWire(c["PitchRange"]   as JsonObject),
                Energy       = c["Energy"] is JsonObject e
                                   ? new Mpai.Core.ValueType
                                     {
                                         Value = (double?)e["Value"],
                                         Type  = (string?)e["Type"]
                                     }
                                   : null,
                Prosody      = (string?)c["Prosody"],
                Disfluencies = (bool?)c["Disfluencies"]
            };

        SpeechStructure? structure = null;
        if (wire["Structure"] is JsonObject st)
            structure = new SpeechStructure
            {
                UtteranceCount = (int?)st["UtteranceCount"],
                TurnBased      = (bool?)st["TurnBased"]
            };

        return new SpeechAttributes
        {
            Source                = (string?)wire["Source"],
            Metadata              = metadata,
            SpeechCharacteristics = characteristics,
            Structure             = structure,
            Device                = DeviceFromWire(wire["Device"] as JsonObject)
        };
    }

    private static ValueUnit? ValueUnitFromWire(
        JsonObject? wire) =>
        wire is null
            ? null
            : new ValueUnit
              {
                  Value = (double?)wire["Value"],
                  Unit  = (string?)wire["Unit"]
              };

    private static AudioDevice? DeviceFromWire(
        JsonObject? wire)
    {
        if (wire is null) return null;

        return new AudioDevice
        {
            DeviceID     = (string?)wire["DeviceID"],
            DeviceRole   = (string?)wire["DeviceRole"],
            DeviceType   = (string?)wire["DeviceType"],
            Manufacturer = (string?)wire["Manufacturer"],
            Model        = (string?)wire["Model"],

            CaptureConfiguration = wire["CaptureConfiguration"] is JsonObject cc
                ? new CaptureConfiguration
                  {
                      ChannelCount = (int?)cc["ChannelCount"],
                      SamplingMode = (string?)cc["SamplingMode"]
                  }
                : null,

            RenderConfiguration = wire["RenderConfiguration"] is JsonObject rc
                ? new RenderConfiguration
                  {
                      ChannelCount  = (int?)rc["ChannelCount"],
                      RenderingMode = (string?)rc["RenderingMode"]
                  }
                : null,

            Synchronisation = wire["Synchronisation"] is JsonObject sy
                ? new Synchronisation
                  {
                      ClockType = (string?)sy["ClockType"],
                      Reference = (string?)sy["Reference"]
                  }
                : null
        };
    }
}