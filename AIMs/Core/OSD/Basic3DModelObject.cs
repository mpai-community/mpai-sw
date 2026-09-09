using System;

using Mpai.Core;   // SpaceTime, VisualQualifier, DataExchangeMetadata (Core primitives)

namespace Mpai.Core.OSD;

// Basic 3D Model Object - OSD/V1.5/data/Basic3DModelObject.json (OSD-B3O),
// schema-correct. A 3D model as an Object. Its Data carries the model (e.g. a
// glTF/GLB, inline or by reference) and its Qualifier is a VisualQualifier
// recording the model's content format. Objects and scenes live in OSD; this new
// type follows that rule (the media Basic objects predate it and are not migrated).
public sealed class Basic3DModelObject
{
    public string Header { get; init; } = "OSD-B3O-V1.5";
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string Basic3DModelObjectID { get; init; } = "";
    public SpaceTime? Basic3DModelObjectSpaceTime { get; init; }

    public byte[] Data { get; init; } = [];                    // inline 3D model data (e.g. GLB/glTF)
    public VisualQualifier? ModelQualifier { get; init; }      // 3DModelQualifier (VisualQualifier)

    public DataExchangeMetadata? DataXMData { get; init; }
    public string? DescrMetadata { get; init; }

    public static Basic3DModelObject FromData(byte[] data, VisualQualifier? qualifier = null) => new()
    {
        Basic3DModelObjectID = Guid.NewGuid().ToString(),
        Data = data,
        ModelQualifier = qualifier
    };
}