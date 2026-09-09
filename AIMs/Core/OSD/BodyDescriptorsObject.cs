using System;
using System.Collections.Generic;
using System.Linq;

namespace Mpai.Core.OSD;

// PAF-BDO-V1.6 - Body Descriptors Object. The standard MPAI type that carries a
// Body's descriptors - "joints and posture of a human or humanoid body" - plus a
// Qualifier (TFA-BDQ) naming the content format. Produced by PAF-EBD (Entity Body
// Description). The body analogue of FaceDescriptorsObject (PAF-FDO).
//
// Mirrors schemas/PAF/V1.6/data/BodyDescriptorsObject.json. The descriptor data is
// a 3D body representation in one of the canonical BodyDescriptorsContentFormats
// (BVH, SMPL, glTF, ...), chosen because 3D posture expresses the body's SEMANTICS
// for Personal Status. This implementation emits a BVH skeleton; the Qualifier
// records ContentFormat = "BVH".
//
// Gesture is a SUBSET of Body: a Gesture Descriptors Object shares the PAF-BDO
// header and the SAME single Qualifier (TFA-BDQ) and content-format enumeration,
// carrying the gesture-relevant subset of the body's joints.
public sealed class BodyDescriptorsObject
{
    public string Header { get; init; } = "PAF-BDO-V1.6";
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string BodyDescriptorsObjectID { get; init; } = "";
    public List<BodyDescriptorsDataItem> BodyDescriptorsData { get; init; } = new();
    public BodyDescriptorsQualifier? BodyDescriptorsQualifier { get; init; }
    public string? DescrMetadata { get; init; }

    // Build from a 3D body representation (the descriptor Data) and its content
    // format (a value from BodyDescriptorsContentFormats, e.g. "BVH").
    public static BodyDescriptorsObject FromContent(string data, string contentFormat)
        => new()
        {
            BodyDescriptorsObjectID = Guid.NewGuid().ToString(),
            BodyDescriptorsData = new List<BodyDescriptorsDataItem>
            {
                new() { Data = data }
            },
            BodyDescriptorsQualifier = BodyDescriptorsQualifier.For(contentFormat)
        };

    // The descriptor data carried inline, if present (e.g. the BVH text).
    public string? Content() => BodyDescriptorsData.FirstOrDefault(d => d.Data is not null)?.Data;

    // The content format recorded by the Qualifier (e.g. "BVH").
    public string? GetContentFormat() => BodyDescriptorsQualifier?.Format?.ContentFormat;
}

public sealed class BodyDescriptorsDataItem
{
    public string? Data { get; init; }        // inline (here: the BVH skeleton text)
    public string? DataURI { get; init; }      // by reference
    public long? DataLength { get; init; }
    public string? DataID { get; init; }       // by identifier
}

// TFA-BDQ-V1.5 - Body Descriptors Qualifier. Shared by Body AND Gesture. Mirrors
// schemas/TFA/V1.5/data/BodyDescriptorsQualifier.json: a Format object whose
// ContentFormat is a value from BodyDescriptorsContentFormats.json.
public sealed class BodyDescriptorsQualifier
{
    public string Header { get; init; } = "TFA-BDQ-V1.5";
    public string BodyDescriptorsQualifierID { get; init; } = "";
    public BodyDescriptorsFormat Format { get; init; } = new();

    public static BodyDescriptorsQualifier For(string contentFormat) => new()
    {
        BodyDescriptorsQualifierID = Guid.NewGuid().ToString(),
        Format = new BodyDescriptorsFormat { ContentFormat = contentFormat }
    };
}

public sealed class BodyDescriptorsFormat
{
    // A value from TFA/V1.5/formats/BodyDescriptorsContentFormats.json
    // (BVH, FBX, glTF, SMPL, SMPL-X, STAR, USD, VRML, X3D, ...).
    public string? ContentFormat { get; init; }
}
