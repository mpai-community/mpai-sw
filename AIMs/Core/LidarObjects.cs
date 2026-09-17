using System.Collections.Generic;

namespace Mpai.Core.OSD;

// First-class LiDAR data types (OSD-BLO, OSD-BLS), defined in Core alongside the
// Audio (OSD-BAO) and Visual (OSD-BVO) objects. Previously these lived inside the
// BLS AIM; a first-class data type belongs in Core, defined once.

// OSD-BLO - Basic LiDAR Object: one LiDAR object's data at the boundary.
public sealed class BasicLiDARObject
{
    public string Header { get; init; } = "OSD-BLO-V1.5";
    public string? MetaverseID { get; init; }
    public string? UEnvironment { get; init; }
    public string BasicLiDARObjectID { get; init; } = "";
    public SimpleTime? BasicLiDARObjectTime { get; init; }
    public SpaceTime? BasicLiDARObjectSpaceTime { get; init; }
    public List<object>? BasicLiDARData { get; init; }
    public string? DescrMetadata { get; init; }
}

// OSD-BLS - Basic LiDAR Scene Descriptors: the described LiDAR scene.
public sealed class BasicLiDARSceneDescriptors
{
    public string Header { get; init; } = "OSD-BLS-V1.5";
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string BasicLiDARSceneDescriptorsID { get; init; } = "";
    public SpaceTime? BasicLiDARSceneDescriptorsSpaceTime { get; init; }
    public int ObjectCount { get; init; }
    public List<BasicLiDARSceneEntry> BasicLiDARSceneDescriptorsEntries { get; init; } = new();
    public string? DescrMetadata { get; init; }
}

public sealed class BasicLiDARSceneEntry
{
    public SpaceTime? ObjectSpaceTime { get; init; }
    public List<object> ObjectIDOrObject { get; init; } = new();
}
