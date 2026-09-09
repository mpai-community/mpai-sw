using System;
using System.Collections.Generic;
using System.Linq;

namespace Mpai.Core.OSD;

// MMC-SDO-V2.5 - Speech Descriptors Object. The standard MPAI type that carries
// an Entity's speech descriptors (here: an ECAPA-TDNN speaker embedding) plus a
// Qualifier saying which descriptor format the Data is. Produced by MMC-ESD
// (Entity Speech Description) and stored, as a serialized standard type, in the
// gallery so any AIM can read it.
//
// Mirrors schemas/MMC/V2.5/data/SpeechDescriptorsObject.json. Speech differs from
// face in the qualifier: the format is recorded under OtherSpeechDescriptorsFormats
// (the NN-model enumeration) rather than the MPAI-native BasicSpeechDescriptors.
public sealed class SpeechDescriptorsObject
{
    public string Header { get; init; } = "MMC-SDO-V2.5";
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string SpeechDescriptorsObjectID { get; init; } = "";
    public SimpleTime? SpeechDescriptorsObjectTime { get; init; }   // schema: SpeechDescriptorsObjectTime
    public List<SpeechDescriptorsDataItem> SpeechDescriptorsData { get; init; } = new();
    public SpeechDescriptorsQualifier? SpeechDescriptorsQualifier { get; init; }
    public string? DescrMetadata { get; init; }

    public static SpeechDescriptorsObject FromEmbedding(float[] embedding, string contentFormat)
        => new()
        {
            SpeechDescriptorsObjectID = Guid.NewGuid().ToString(),
            SpeechDescriptorsData = new List<SpeechDescriptorsDataItem>
            {
                new() { Data = EmbeddingCodec.ToBase64(embedding) }
            },
            SpeechDescriptorsQualifier = SpeechDescriptorsQualifier.ForOther(contentFormat)
        };

    public float[]? Embedding()
    {
        var inline = SpeechDescriptorsData.FirstOrDefault(d => d.Data is not null)?.Data;
        return inline is null ? null : EmbeddingCodec.FromBase64(inline);
    }
}

public sealed class SpeechDescriptorsDataItem
{
    public string? Data { get; init; }        // inline, base64
    public string? DataURI { get; init; }      // by reference
    public long? DataLength { get; init; }
    public string? DataID { get; init; }       // by identifier
}

// TFA-SDQ-V1.5 - the qualifier. Its Formats offers TWO sources: the MPAI-native
// BasicSpeechDescriptors (Pitch/Intensity/Tempo), or Other (an NN-model format
// from SpeechDescriptorsFormats.json, e.g. "ECAPA-TDNN (192-d)"). For an NN
// embedding we record the Other format.
public sealed class SpeechDescriptorsQualifier
{
    public string Header { get; init; } = "TFA-SDQ-V1.5";
    public string SpeechDescriptorsQualifierID { get; init; } = "";
    public SpeechDescriptorsFormats Formats { get; init; } = new();

    public static SpeechDescriptorsQualifier ForOther(string otherFormat) => new()
    {
        SpeechDescriptorsQualifierID = Guid.NewGuid().ToString(),
        Formats = new SpeechDescriptorsFormats { OtherSpeechDescriptorsFormats = otherFormat }
    };
}

public sealed class SpeechDescriptorsFormats
{
    // MPAI-native basic descriptors (Pitch/Intensity/Tempo) - unused for NN embeddings.
    public object? MPAISpeechDescriptorsFormat { get; init; }
    // A value from TFA/V1.5/formats/SpeechDescriptorsFormats.json (the NN enumeration).
    public string? OtherSpeechDescriptorsFormats { get; init; }
}

// Packs/unpacks a float[] embedding to/from a base64 string, little-endian
// float32. Shared by the face and speech descriptor objects so both encode their
// Data identically.
public static class EmbeddingCodec
{
    public static string ToBase64(float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        return Convert.ToBase64String(bytes);
    }

    public static float[] FromBase64(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }
}
