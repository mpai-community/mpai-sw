using Mpai.Core;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Mpai.Core.OSD;

// ---------------------------------------------------------------------------
//  Basic 3D Model Scene Descriptors (OSD-B3S) - OSD/V1.5/data/Basic3DModelSceneDescriptors.json
//  3D Model Objects, each placed by its Space-Time, seen from a UserPoV. A member
//  is a Basic 3D Model Object or its identifier: the schema's array of one, kept
//  as JSON so a member passes through unchanged.
// ---------------------------------------------------------------------------
public sealed class Basic3DModelSceneDescriptors
{
    public string Header { get; init; } = "OSD-B3S-V1.5";
    public string MInstanceID { get; init; } = "";
    public string? UEnvironmentID { get; init; }
    public string Basic3DModelSceneDescriptorsID { get; init; } = "";
    public SimpleTime? Basic3DModelSceneDescriptorsTime { get; init; }
    public PointOfView UserPoV { get; init; } = new();
    public double? GravityValue { get; init; }
    public SpaceTime Basic3DModelSceneDescriptorsSpaceTime { get; init; } = new();

    [JsonPropertyName("3DModelObjectCount")]
    public int ModelObjectCount { get; init; }

    // The schema's Basic3DModelSceneDescriptors; named "...Items" in C# so the
    // property does not share a name with its class.
    [JsonPropertyName("Basic3DModelSceneDescriptors")]
    public List<Basic3DModelSceneItem> Basic3DModelSceneItems { get; init; } = new();

    public DataExchangeMetadata? DataXMData { get; init; }
    public string? DescrMetadata { get; init; }
}

public sealed class Basic3DModelSceneItem
{
    [JsonPropertyName("3DModelObjectSpaceTime")]
    public SpaceTime? ModelObjectSpaceTime { get; init; }

    // A Basic 3D Model Object, or its identifier.
    [JsonPropertyName("3ObjectIDOr3Object")]
    public JsonArray ObjectIDOrObject { get; init; } = new();

    // Where this member is seen from; absent, the Scene's UserPoV applies.
    public PointOfView? UserPoV { get; init; }

    [JsonPropertyName("3DModelSceneEnrichment")]
    public JsonArray? Enrichment { get; init; }

    // The member's identifier: the string itself, or the Object's ID.
    [JsonIgnore]
    public string? Id => ObjectIDOrObject.FirstOrDefault() switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonObject o => (string?)o["Basic3DModelObjectID"],
        _ => null
    };
}

// ---------------------------------------------------------------------------
//  Basic Speech Scene Descriptors (OSD-BSS) - OSD/V1.5/data/BasicSpeechSceneDescriptors.json
//  Basic Speech Objects, each placed by its Space-Time, heard from a UserPoV.
// ---------------------------------------------------------------------------
public sealed class BasicSpeechSceneDescriptors
{
    public string Header { get; init; } = "OSD-BSS-V1.5";
    public string MInstanceID { get; init; } = "";
    public string? UEnvironmentID { get; init; }
    public string BasicSpeechSceneDescriptorsID { get; init; } = "";
    public SimpleTime? BasicSpeechSceneDescriptorsTime { get; init; }
    public SpaceTime BasicSpeechSceneDescriptorsSpaceTime { get; init; } = new();
    public PointOfView? UserPoV { get; init; }
    public JsonArray? ClosedSpace { get; init; }
    public AcousticProfile? AcousticProfile { get; init; }
    public int ObjectCount { get; init; }

    [JsonPropertyName("BasicSpeechSceneDescriptors")]
    public List<BasicSpeechSceneItem> BasicSpeechSceneItems { get; init; } = new();

    public DataExchangeMetadata? DataXMData { get; init; }
    public string? DescrMetadata { get; init; }
}

public sealed class BasicSpeechSceneItem
{
    public SpaceTime? ObjectSpaceTime { get; init; }
    public PointOfView? UserPoV { get; init; }

    // The member: in C# the Basic Speech Object itself; in JSON the schema's
    // ObjectIDOrObject, an array of one.
    [JsonIgnore]
    public BasicSpeechObject? SpeechObject { get; init; }
    [JsonPropertyName("ObjectIDOrObject")]
    public List<BasicSpeechObject>? ObjectIDOrObject
    {
        get => SpeechObject is null ? null : [SpeechObject];
        init => SpeechObject = value?.FirstOrDefault();
    }
}
