// TODO [architecture, flagged]: OSD is a distinct MPAI standard (Object and
// Scene Description). These OSD data types currently live inside Mpai.Core
// (namespace Mpai.Core.OSD) for convenience, but should later be extracted
// into their own Mpai.Osd project - mirroring how MMC/CAE/CVE/PTF are
// separated - in a single deliberate refactor covering all OSD types
// (audio + visual + LiDAR) at once, not piecemeal.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

using Mpai.Core;

namespace Mpai.Core.OSD;

// ---------------------------------------------------------------------------
//  Schema-accurate projection of the MPAI-OSD V1.5 Basic Visual Scene
//  descriptor, the visual counterpart of BasicAudioSceneDescriptors.
//
//  BasicVisualObject is NOT duplicated here - it lives as the canonical,
//  schema-correct type directly in Mpai.Core (Objects.cs). This file holds
//  only the visual scene layer that the visual AVS pipeline (and, later, VOI)
//  emits.
//
//  DESCRIBE, DO NOT IDENTIFY: this type carries visual objects (faces, bodies,
//  generic objects) with their Spatial Attitude (the member's SpaceTime).
//  It says "a face is here, at this bearing" - never "this is person X".
//  Identity is the job of downstream FIR, not of the scene descriptor.
//
//  Where a oneOf choice in the schema is "the object itself, or the string ID
//  of one already in the Repository", this follows the same simplification as
//  BasicAudioSceneEntry: a single nullable object-typed property, not a strict
//  discriminated union. Resolving an ID reference is a Repository lookup, not
//  something the data type does.
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
//  Basic Visual Scene Descriptors - OSD/V1.5/data/BasicVisualSceneDescriptors.json
//  A flat-ish scene: each entry places a BasicVisualObject (or its id) by its
//  SpaceTime. Since 2026/10/02 (the author) the members are Basic Visual
//  Objects only, and the Scene is seen from its UserPoV (required, as in the
//  Basic Audio Scene); a member's own UserPoV overrides it.
// ---------------------------------------------------------------------------
public sealed class BasicVisualSceneDescriptors
{
    public string Header { get; init; } = "OSD-BVS-V1.5";   // normative data-type label (Basic Visual Scene)
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string BasicVisualSceneDescriptorsID { get; init; } = "";
    public SimpleTime? BVSDescriptorsTime { get; init; }

    public SpaceTime? BVSDescriptorsSpaceTime { get; init; }
    // Where the Scene is seen from - for a captured Scene, the camera.
    public PointOfView UserPoV { get; init; } = new();
    public double? GravityValue { get; init; }

    public int VisualObjectCount { get; init; }
    // Named "...Entries" rather than the schema's literal
    // "BasicVisualSceneDescriptors" to avoid a property sharing the exact name
    // of its own containing class - same convention as
    // BasicAudioSceneDescriptorsEntries.
    [JsonPropertyName("BasicVisualSceneDescriptors")]
    public List<BasicVisualSceneEntry> BasicVisualSceneDescriptorsEntries { get; init; } = new();

    public DataExchangeMetadata? DataXMData { get; init; }
    public string? DescrMetadata { get; init; }
}

public sealed class BasicVisualSceneEntry
{
    // Where the member is in the Scene.
    public SpaceTime? VisualObjectSpaceTime { get; init; }
    // Where this member is seen from; absent, the Scene's UserPoV applies.
    public PointOfView? UserPoV { get; init; }

    // The member: in C# the Basic Visual Object itself; in JSON the schema's
    // VObjectIDOrVObject, an array of one.
    [JsonIgnore]
    public BasicVisualObject? VObjectIDOrVObject { get; init; }
    [JsonPropertyName("VObjectIDOrVObject")]
    public List<BasicVisualObject>? VObjectIDOrVObjectArray
    {
        get => VObjectIDOrVObject is null ? null : [VObjectIDOrVObject];
        init => VObjectIDOrVObject = value?.FirstOrDefault();
    }
}
