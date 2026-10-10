using System;
using System.Collections.Generic;
using System.Linq;

namespace Mpai.Core.OSD;

// MMC-TDO-V2.5 - Text Descriptors Object. The standard MPAI type that carries a
// Text's descriptors (its Meaning) plus a Qualifier saying which descriptor format
// the Data is. Produced by MMC-NLU (Natural Language Understanding).
//
// Mirrors schemas/MMC/V2.5/data/TextDescriptorsObject.json, and is the text
// analogue of SpeechDescriptorsObject (MMC-SDO). The Data of the Object is what the
// producer computed; MMC-NLU computes a structured linguistic analysis (the POS, NE,
// dependency and SRL taggings of TextTaggings) and carries it inline as JSON. An NN
// text embedding would be recorded under OtherTextDescriptorsFormats of the Qualifier.
public sealed class TextDescriptorsObject
{
    public string Header { get; init; } = "MMC-TDO-V2.5";
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string TextDescriptorsObjectID { get; init; } = "";
    public List<TextDescriptorsDataItem> TextDescriptorsData { get; init; } = new();
    public TextDescriptorsQualifier? TextDescriptorsQualifier { get; init; }
    public string? DescrMetadata { get; init; }

    // Build a Text Descriptors Object from the four taggings, carried inline in the
    // Data (JSON).
    public static TextDescriptorsObject FromTaggings(TextTaggings taggings)
        => new()
        {
            TextDescriptorsObjectID = Guid.NewGuid().ToString(),
            TextDescriptorsData = new List<TextDescriptorsDataItem>
            {
                new() { Data = MpaiJson.ToJson(taggings) }
            },
            TextDescriptorsQualifier = new TextDescriptorsQualifier
            {
                TextDescriptorsQualifierID = Guid.NewGuid().ToString()
            }
        };

    // The four taggings held inline in the Data, if there are any.
    public TextTaggings? Taggings()
    {
        var inline = TextDescriptorsData.FirstOrDefault(d => d.Data is not null)?.Data;
        return inline is null ? null : MpaiJson.FromJson<TextTaggings>(inline);
    }
}

public sealed class TextDescriptorsDataItem
{
    public string? Data { get; init; }        // inline (here: the taggings as JSON)
    public string? DataURI { get; init; }      // by reference
    public long? DataLength { get; init; }
    public string? DataID { get; init; }       // by identifier
}

// The linguistic analysis of a text as four taggings (any may be null, per the
// MMC-NLU spec). It is not a standard Data Type: it is the content that MMC-NLU
// puts in the Data of a Text Descriptors Object.
public sealed class TextTaggings
{
    public TextDescriptorsData TextDescriptorsData { get; init; } = new();
}

// The four taggings. Each is a { set, result } pair; any may be null.
public sealed class TextDescriptorsData
{
    public Tagging? POS_tagging { get; init; }
    public Tagging? NE_tagging { get; init; }
    public Tagging? dependency_tagging { get; init; }
    public Tagging? SRL_tagging { get; init; }
}

public sealed class Tagging
{
    public string? Set { get; init; }       // Identifier of the tagging set used
    public string? Result { get; init; }     // The tagging result for the input text
}

// TFA-TDQ-V1.5 - the qualifier. Its Formats offers an NN-model format from
// TextDescriptorsFormats.json, e.g. "BERT (base, 768-d)"; for the taggings of
// MMC-NLU there is none, and Formats is empty.
public sealed class TextDescriptorsQualifier
{
    public string Header { get; init; } = "TFA-TDQ-V1.5";
    public string TextDescriptorsQualifierID { get; init; } = "";
    public TextDescriptorsFormats Formats { get; init; } = new();

    public static TextDescriptorsQualifier ForOther(string otherFormat) => new()
    {
        TextDescriptorsQualifierID = Guid.NewGuid().ToString(),
        Formats = new TextDescriptorsFormats { OtherTextDescriptorsFormats = otherFormat }
    };
}

public sealed class TextDescriptorsFormats
{
    // A value from TFA/V1.5/formats/TextDescriptorsFormats.json (the NN enumeration).
    public string? OtherTextDescriptorsFormats { get; init; }
}
