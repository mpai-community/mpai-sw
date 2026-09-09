using System;
using System.Collections.Generic;
using System.Linq;
namespace Mpai.Core.OSD;
// PAF-FDO-V1.6 - Face Descriptors Object. The standard MPAI type that carries an
// Entity's face descriptors (e.g. an ArcFace embedding, or a FACS Action Unit
// frame) plus a Qualifier saying which descriptor format the Data is. Produced by
// PAF-EFD (Entity Face Description, analysis) and PAF-GFD (Generative Face
// Description, generation).
//
// Mirrors schemas/PAF/V1.6/data/FaceDescriptorsObject.json. FaceDescriptorsData is
// an array of items; each item optionally carries a Time, so the array is an
// ANIMATION TIMELINE: a sequence of face descriptors over time. A single item
// without Time is a static (single-pose) face. The Time is format-independent - it
// applies whatever the content format (embedding, FACS AU, blendshapes) the
// Qualifier declares.
public sealed class FaceDescriptorsObject
{
    public string Header { get; init; } = "PAF-FDO-V1.6";
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string FaceDescriptorsObjectID { get; init; } = "";
    public SimpleTime? FaceDescriptorsObjectTime { get; init; }
    public List<FaceDescriptorsDataItem> FaceDescriptorsData { get; init; } = new();
    public FaceDescriptorsQualifier? FaceDescriptorsQualifier { get; init; }
    public string? DescrMetadata { get; init; }
    // Build an FDO from an embedding vector and the model that produced it (analysis).
    public static FaceDescriptorsObject FromEmbedding(float[] embedding, string contentFormat)
        => new()
        {
            FaceDescriptorsObjectID = Guid.NewGuid().ToString(),
            FaceDescriptorsData = new List<FaceDescriptorsDataItem>
            {
                new() { Data = EmbeddingCodec.ToBase64(embedding) }
            },
            FaceDescriptorsQualifier = FaceDescriptorsQualifier.For(contentFormat)
        };
    // The embedding carried inline, or null if this object references its data
    // elsewhere (DataURI/DataID) rather than inlining it.
    public float[]? Embedding()
    {
        var inline = FaceDescriptorsData.FirstOrDefault(d => d.Data is not null)?.Data;
        return inline is null ? null : EmbeddingCodec.FromBase64(inline);
    }
}
// One FaceDescriptorsData item: a Time (present when the array is a timeline) plus
// the face descriptors for that instant - inline (Data), by reference (DataURI+
// DataLength), or by identifier (DataID). Matches the schema's item shape.
public sealed class FaceDescriptorsDataItem
{
    public SimpleTime? Time { get; init; }     // frame time within the animation (timeline)
    public string? Data { get; init; }        // inline (e.g. base64 embedding, or a JSON AU frame)
    public string? DataURI { get; init; }      // by reference
    public long? DataLength { get; init; }
    public string? DataID { get; init; }       // by identifier
}
// TFA-FDQ-V1.5 - the qualifier. Its ContentFormat records which descriptor format
// the Data is (e.g. "ArcFace (ResNet-100, 512-d)" or "FACS-AU").
public sealed class FaceDescriptorsQualifier
{
    public string Header { get; init; } = "TFA-FDQ-V1.5";
    public string FaceDescriptorsQualifierID { get; init; } = "";
    public FaceDescriptorsFormats Formats { get; init; } = new();
    public static FaceDescriptorsQualifier For(string contentFormat) => new()
    {
        FaceDescriptorsQualifierID = Guid.NewGuid().ToString(),
        Formats = new FaceDescriptorsFormats { ContentFormat = contentFormat }
    };
}
public sealed class FaceDescriptorsFormats
{
    // A value from TFA/V1.5/formats/FaceDescriptorsContentFormats.json.
    public string? ContentFormat { get; init; }
}
