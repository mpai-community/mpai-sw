// TODO [architecture, flagged]: OSD is a distinct MPAI standard (Object and
// Scene Description). These OSD data types currently live inside Mpai.Core
// (namespace Mpai.Core.OSD) for convenience, but should later be extracted
// into their own Mpai.Osd project - mirroring how MMC/CAE/CVE/PTF are
// separated - in a single deliberate refactor covering all OSD types
// (audio + visual + audio-visual + LiDAR) at once, not piecemeal.

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

using Mpai.Core;

namespace Mpai.Core.OSD;

// ---------------------------------------------------------------------------
//  Basic Audio-Visual Scene Descriptors - OSD/V1.5/data/
//  BasicAudioVisualSceneDescriptors.json, header OSD-BMS-V1.5.
//
//  The multimodal (fused) Basic scene. A BMS is a COMPOSITION of an arbitrary
//  number of per-modality Basic Scene Descriptors (BAS, BVS, BLS, BSS, ...),
//  each carrying its own SpaceTime so the modality scenes stay SPATIALLY
//  ALIGNED in a common frame (the outer BAVSDescriptorsSpaceTime). E.g. a BMS
//  might hold a BAS + a BVS + a BLS, aligned, so "this face" (visual) and
//  "this voice direction" (audio) are registered to the same space.
//
//  NOT RECURSIVE (the author, 2026/10/02): a BMS holds Basic Scenes only, of
//  any medium, none required but at least one; a BMS is not nested in another
//  - that is the Audio-Visual Scene Descriptors (MSD) - and a Basic Object is
//  not a member.
//
//  The schema's per-entry choice is anyOf[ the modality Basic Scenes | an ID
//  string ]. Following the same simplification used by the audio/visual
//  scene types, the C# entry carries a single nullable object reference to the
//  contained scene (its concrete BXS type checked at use), not a strict
//  discriminated union - since the modality Basic-scene types share no common
//  base in this codebase (there is no object hierarchy; they are all Basic).
// ---------------------------------------------------------------------------
public sealed class BasicAudioVisualSceneDescriptors
{
    public string Header { get; init; } = "OSD-BMS-V1.5";
    public string MInstanceID { get; init; } = "";
    public string? UEnvironmentID { get; init; }
    public string BasicAVSceneDescriptorsID { get; init; } = "";
    public SimpleTime? BAVSDescriptorsTime { get; init; }

    // The common reference frame for all contained modality scenes.
    public SpaceTime? BAVSDescriptorsSpaceTime { get; init; }

    // Where the Scene is heard and seen from, the Closed Space (OSD-CSP) it is
    // in, and the Scene part of the Acoustic Profile - optional, added 2026/10/02
    // so that a BMS can hold speech and audio together for CAE-ASM.
    public PointOfView? UserPoV { get; init; }
    public JsonArray? ClosedSpace { get; init; }
    public AcousticProfile? AcousticProfile { get; init; }

    public double? GravityValue { get; init; }

    public int AVObjectCount { get; init; }

    // Named "...Data" to match the schema property BasicAVSceneDescriptorsData. Null
    // when the Scene holds only Objects: the schema wants it absent rather than empty.
    public List<BasicAVSceneEntry>? BasicAVSceneDescriptorsData { get; init; } = new();

    // The Basic Objects placed in the Scene directly, of any medium (the author,
    // 2026/10/05: BME integrates objects and scenes). Null when there are none.
    public List<BasicAVObjectEntry>? BasicObjectsData { get; init; }

    // OPTIONAL alignment layer (populated by the OSD-AVA AIM, absent in the
    // degenerate composition-only BMS). Each element combines the constituent
    // per-modality objects that AVA determined to be the SAME entity (matched by
    // Spatial Attitude / PointOfView), tied by a shared AlignmentCode. Additive
    // and non-destructive: the original BAS/BVS scenes above keep their objects
    // and PointOfViews exactly as measured; this is AVA's interpretation on top.
    public List<AlignedMMObject> AlignedMMObjects { get; init; } = new();

    public DataExchangeMetadata? DataXMData { get; init; }
    public string? DescrMetadata { get; init; }
}

public sealed class BasicAVSceneEntry
{
    // SPATIAL ALIGNMENT: this modality scene's placement within the BMS's common
    // frame. Required - it is what keeps the fused modalities registered.
    public SpaceTime BXSSpaceTime { get; init; } = new();

    // Where this member is heard and seen from; absent, the Scene's UserPoV.
    public PointOfView? UserPoV { get; init; }

    // The contained Basic Scene Descriptors (BAS / BSS / BVS / B3S / BLS / BRS /
    // BUS / BOS), or its ID string. Typed as object because these share no
    // common base type (all Basic, no hierarchy); the concrete type is checked
    // at use (e.g. `entry.BXSOrBXSID is BasicAudioSceneDescriptors`).
    public object? BXSOrBXSID { get; init; }
}

// A Basic Object placed in a Basic Multimodal Scene directly: its placement in the
// common frame, where it is heard and seen from, and the Object (BAO / BVO / BSO /
// B3O / BLO / BRO / BUO / BOO) or its ID string.
public sealed class BasicAVObjectEntry
{
    public SpaceTime BXOSpaceTime { get; init; } = new();
    public PointOfView? UserPoV { get; init; }
    public object? BXOOrBXOID { get; init; }
}

// ---------------------------------------------------------------------------
//  Aligned MultiModal Object - one element of BMS.AlignedMMObjects, produced by
//  OSD-AVA. Combines the constituent per-modality objects (a BasicAudioObject +
//  a BasicVisualObject, extensible to speech/3D) that AVA aligned as the same
//  entity, by a shared AlignmentCode, with an optional consensus PointOfView.
//
//  AlignedObjects holds the constituents (or their id strings). Typed as object
//  because the constituents are of different object types with no common base
//  (BasicAudioObject / BasicVisualObject / BasicSpeechObject / ... / id string);
//  the concrete type is checked at use.
// ---------------------------------------------------------------------------
public sealed class AlignedMMObject
{
    // Shared code marking the constituents as the same entity in this scene.
    public string AlignmentCode { get; init; } = "";

    // Optional consensus attitude of the fused entity. Does NOT replace the
    // constituents' own PointOfViews (those stay on the original scene entries).
    public PointOfView? AlignmentPointOfView { get; init; }

    // How likely it is that the constituents are the same entity, [0, 1]; e.g. a
    // horn heard by a microphone array of limited accuracy, aligned to a car. Two
    // alignments competing for one constituent each carry their own. Optional, as
    // every Confidence (the author, 2026/10/03).
    public double? Confidence { get; init; }

    // The constituent objects fused into this entity (audio + visual, extensible),
    // or their id-string references. At least one; typically two.
    public List<object> AlignedObjects { get; init; } = new();
}
