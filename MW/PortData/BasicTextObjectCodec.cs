using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Mpai.Core;

namespace Mpai.Mas.PortData;

// OSD-BTO-V1.5 - Basic Text Object.
//
// Written against OSD/V1.5/data/BasicTextObject.json and the Text Qualifier it
// references, TFA/V1.5/data/TextQualifier.json.
//
// ALL THREE DATA VARIANTS ARE CARRIED, unlike the speech codec. BasicTextData is
// already a list of the schema's three shapes in the C# - InlineTextData,
// ReferencedData, IdentifiedData - so a reference survives a round trip instead
// of being refused. Text is small enough that the inline form is what anything
// actually sends, but the mapping costs nothing when the type is already there.
//
// NOTE two differences from the Basic Speech Object schema, which are the
// schemas' and not ours: the reference variant here is { Length, DataURI }
// where speech says { DataLength, DataURI }, and the Formats member here is
// ContentFormat singular where speech has ContentFormats plural. A codec per
// Data Type absorbs that; a single shared translator could not.
public sealed class BasicTextObjectCodec : IPortDataCodec
{
    public string DataType => "OSD-BTO-V1.5";

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition =
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public byte[] ToWire(
        string internalJson)
    {
        var text = MpaiJson.FromJson<BasicTextObject>(internalJson);

        var items = new JsonArray();
        foreach (var item in text.BasicTextData)
            items.Add(item switch
            {
                InlineTextData inline =>
                    new JsonObject { ["Data"] = inline.Data },

                ReferencedData reference =>
                    new JsonObject
                    {
                        ["Length"]  = reference.Length,
                        ["DataURI"] = reference.DataURI
                    },

                IdentifiedData identified =>
                    new JsonObject { ["ID"] = identified.ID },

                _ => throw new NotSupportedException(
                         $"Unknown Basic Text data item: {item.GetType().Name}.")
            });

        var wire = new JsonObject
        {
            // Pinned by the schema, not chosen by the sender.
            ["Header"] = "OSD-BTO-V1.5",

            // Required, alongside Header.
            ["BasicTextObjectID"] =
                string.IsNullOrEmpty(text.BasicTextObjectID)
                    ? Guid.NewGuid().ToString()
                    : text.BasicTextObjectID,

            ["BasicTextData"] = items
        };

        if (!string.IsNullOrEmpty(text.MInstanceID))
            wire["MInstanceID"] = text.MInstanceID;

        if (!string.IsNullOrEmpty(text.UEnvironmentID))
            wire["UEnvironmentID"] = text.UEnvironmentID;

        if (!string.IsNullOrEmpty(text.DescrMetadata))
            wire["DescrMetadata"] = text.DescrMetadata;

        // BasicTextObjectSpaceTime is not emitted: the schema refs a SimpleTime,
        // the C# declares a SpaceTime, and nothing assigns it. Same reasoning as
        // SpeechQualifierTime.

        if (text.TextQualifier is not null)
            wire["TextQualifier"] = QualifierToWire(text.TextQualifier);

        return Encoding.UTF8.GetBytes(wire.ToJsonString(WireOptions));
    }

    public string ToInternal(
        byte[] wire)
    {
        var root = JsonNode.Parse(Encoding.UTF8.GetString(wire)) as JsonObject
                   ?? throw new FormatException(
                          "Basic Text Object port-data is not a JSON object.");

        var items = new List<BasicTextDataItem>();
        if (root["BasicTextData"] is JsonArray entries)
            foreach (var entry in entries)
            {
                if (entry is not JsonObject o) continue;

                if (o["Data"] is not null)
                    items.Add(new InlineTextData((string)o["Data"]!));
                else if (o["DataURI"] is not null)
                    items.Add(new ReferencedData(
                        (long?)o["Length"] ?? 0L, (string)o["DataURI"]!));
                else if (o["ID"] is not null)
                    items.Add(new IdentifiedData((string)o["ID"]!));
            }

        var text = new BasicTextObject
        {
            MInstanceID       = (string?)root["MInstanceID"],
            UEnvironmentID    = (string?)root["UEnvironmentID"],
            BasicTextObjectID = (string?)root["BasicTextObjectID"] ?? string.Empty,
            DescrMetadata     = (string?)root["DescrMetadata"],
            BasicTextData     = items,
            TextQualifier     = QualifierFromWire(root["TextQualifier"] as JsonObject)
        };

        return MpaiJson.ToJson(text);
    }

    private static JsonObject QualifierToWire(
        TextQualifier q)
    {
        var wire = new JsonObject
        {
            ["Header"]          = "TFA-TXQ-V1.5",
            ["TextQualifierID"] = string.IsNullOrEmpty(q.TextQualifierID)
                                      ? Guid.NewGuid().ToString()
                                      : q.TextQualifierID,

            // Required by the schema. Unlike the Speech Qualifier''s SubTypes,
            // this one is an open object - but nothing in the code has anything
            // to put in it, so it goes out empty.
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

        if (q.Attributes is { } a)
        {
            var attributes = new JsonObject();

            if (a.Language is { } language)
            {
                var l = new JsonObject();
                if (!string.IsNullOrEmpty(language.LanguageCode))
                    l["LanguageCode"] = language.LanguageCode;
                if (!string.IsNullOrEmpty(language.LanguageFormat))
                    l["LanguageFormat"] = language.LanguageFormat;
                if (l.Count > 0) attributes["Language"] = l;
            }

            // NOT CARRIED: Attributes.ObjectIdentifier, an OSD
            // InstanceIdentifier. Same gap as the Speech Qualifier''s
            // SpeakerIdentity, and it closes with the same codec.

            if (attributes.Count > 0) wire["Attributes"] = attributes;
        }

        return wire;
    }

    private static JsonObject FormatsToWire(
        TextFormat? format)
    {
        var wire = new JsonObject();

        // ContentFormat, singular, per this schema.
        if (!string.IsNullOrEmpty(format?.ContentFormat?.Static))
            wire["ContentFormat"] = new JsonObject
            {
                ["Static"] = format!.ContentFormat!.Static
            };

        return wire;
    }

    private static TextQualifier? QualifierFromWire(
        JsonObject? wire)
    {
        if (wire is null) return null;

        TextFormat? format = null;
        if (wire["Formats"]?["ContentFormat"]?["Static"] is { } stat)
            format = new TextFormat
            {
                ContentFormat = new TextContentFormat { Static = (string?)stat }
            };

        TextAttributes? attributes = null;
        if (wire["Attributes"]?["Language"] is JsonObject l)
            attributes = new TextAttributes
            {
                Language = new Language
                {
                    LanguageCode   = (string?)l["LanguageCode"],
                    LanguageFormat = (string?)l["LanguageFormat"]
                }
            };

        return new TextQualifier
        {
            MInstanceID     = (string?)wire["MInstanceID"],
            UEnvironmentID  = (string?)wire["UEnvironmentID"],
            TextQualifierID = (string?)wire["TextQualifierID"] ?? string.Empty,
            DescrMetadata   = (string?)wire["DescrMetadata"],
            Format          = format,
            Attributes      = attributes
        };
    }
}