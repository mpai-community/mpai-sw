using System;
using System.Collections.Generic;
using System.Linq;

namespace Mpai.Core.OSD;

// MMC-TDO-V2.5 - Text Descriptors Object. The standard MPAI type that carries a
// Text's descriptors (its Meaning) plus a Qualifier saying which descriptor format
// the Data is. Produced by MMC-NLU (Natural Language Understanding).
//
// Mirrors schemas/MMC/V2.5/data/TextDescriptorsObject.json, and is the text
// analogue of SpeechDescriptorsObject (MMC-SDO). Text differs from speech in the
// PRIMARY format: the MPAI-native Basic Text Descriptors is a STRUCTURED linguistic
// analysis (POS/NE/dependency/SRL taggings), not an NN embedding. So the Basic
// content is a first-class object here, recorded under MPAITextDescriptorsFormat;
// an NN text embedding would instead go under OtherTextDescriptorsFormats.
public sealed class TextDescriptorsObject
{
    public string Header { get; init; } = "MMC-TDO-V2.5";
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string TextDescriptorsObjectID { get; init; } = "";
    public List<TextDescriptorsDataItem> TextDescriptorsData { get; init; } = new();
    public TextDescriptorsQualifier? TextDescriptorsQualifier { get; init; }
    public string? DescrMetadata { get; init; }

    // Build a Text Descriptors Object from the MPAI-native Basic descriptors (the
    // four taggings). The taggings are carried in the Qualifier's MPAI format, and
    // also serialized into the Data array (inline JSON) so a reader that only looks
    // at Data still gets them.
    public static TextDescriptorsObject FromBasic(BasicTextDescriptors basic)
        => new()
        {
            TextDescriptorsObjectID = Guid.NewGuid().ToString(),
            TextDescriptorsData = new List<TextDescriptorsDataItem>
            {
                new() { Data = MpaiJson.ToJson(basic) }
            },
            TextDescriptorsQualifier = TextDescriptorsQualifier.ForBasic(basic)
        };

    // The Basic (four-tagging) descriptors, whether held in the Qualifier or inline.
    public BasicTextDescriptors? Basic()
    {
        if (TextDescriptorsQualifier?.Formats.MPAITextDescriptorsFormat is { } fromQualifier)
            return fromQualifier;
        var inline = TextDescriptorsData.FirstOrDefault(d => d.Data is not null)?.Data;
        return inline is null ? null : MpaiJson.FromJson<BasicTextDescriptors>(inline);
    }
}

public sealed class TextDescriptorsDataItem
{
    public string? Data { get; init; }        // inline (here: the Basic taggings as JSON)
    public string? DataURI { get; init; }      // by reference
    public long? DataLength { get; init; }
    public string? DataID { get; init; }       // by identifier
}

// MMC-TPD-V2.5 - Basic Text Descriptors: the MPAI-native text descriptor format,
// the linguistic analysis of a text as four taggings (any may be null, per the
// MMC-NLU spec). Mirrors schemas/MMC/V2.5/data/BasicTextDescriptors.json.
public sealed class BasicTextDescriptors
{
    public string Header { get; init; } = "MMC-TPD-V2.5";
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string BasicTextDescriptorsID { get; init; } = "";
    public TextDescriptorsData TextDescriptorsData { get; init; } = new();
    public string? DescrMetadata { get; init; }
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

// TFA-TDQ-V1.5 - the qualifier. Its Formats offers TWO sources: the MPAI-native
// BasicTextDescriptors (the four taggings), or Other (an NN-model format from
// TextDescriptorsFormats.json, e.g. "BERT (base, 768-d)"). For the Basic taggings
// we record the MPAI format; for an NN embedding we record the Other format.
public sealed class TextDescriptorsQualifier
{
    public string Header { get; init; } = "TFA-TDQ-V1.5";
    public string TextDescriptorsQualifierID { get; init; } = "";
    public TextDescriptorsFormats Formats { get; init; } = new();

    public static TextDescriptorsQualifier ForBasic(BasicTextDescriptors basic) => new()
    {
        TextDescriptorsQualifierID = Guid.NewGuid().ToString(),
        Formats = new TextDescriptorsFormats { MPAITextDescriptorsFormat = basic }
    };

    public static TextDescriptorsQualifier ForOther(string otherFormat) => new()
    {
        TextDescriptorsQualifierID = Guid.NewGuid().ToString(),
        Formats = new TextDescriptorsFormats { OtherTextDescriptorsFormats = otherFormat }
    };
}

public sealed class TextDescriptorsFormats
{
    // MPAI-native basic descriptors (the four taggings).
    public BasicTextDescriptors? MPAITextDescriptorsFormat { get; init; }
    // A value from TFA/V1.5/formats/TextDescriptorsFormats.json (the NN enumeration).
    public string? OtherTextDescriptorsFormats { get; init; }
}
