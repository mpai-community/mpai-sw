using System;
using System.Collections.Generic;

using Mpai.Core;

namespace Mpai.Core.OSD;

// ---------------------------------------------------------------------------
//  Schema-accurate projections of the MPAI-OSD V1.5 Audio composite/scene
//  data types, for use by ASM (AOE/ASE/ASD) and the Repository.
//
//  BasicAudioObject and AcousticProfile are NOT duplicated here - they now
//  live as the canonical, schema-correct types directly in Mpai.Core
//  (Objects.cs / Qualifiers.cs), used by both the MMC-AMQ pipeline and ASM.
//  This file only holds the composite layer (AudioObject, BasicAudioScene
//  Descriptors, AudioSceneDescriptors) that only ASM needs.
//
//  Where a oneOf choice in the schema is "the object itself, or the string ID
//  of one already in the Repository", this follows the same simplification
//  already used by Mpai.Core.TextObject's BasicTextObjectEntry/
//  SubTextObjectEntry: a single nullable object-typed property, not a strict
//  discriminated union. Resolving an ID reference to the actual object is a
//  Repository lookup (GetAsset), not something the data type itself does.
// ---------------------------------------------------------------------------

// Referenced by these schemas but not yet provided. Kept here as explicit,
// commented placeholders rather than silently treating fields as untyped.
// (SimpleTime and SpaceTime now live in Mpai.Core - referenced across every
// modality, not just audio.)
// (PointOfView now lives in Mpai.Core - it's cross-modality, not audio-specific;
// its own MediaType field spans Speech/Audio/Visual/Audio-Visual/etc.)
public sealed class Trace { }             // AIF/V3.0/data/Trace.json
public sealed class Depth { }             // OSD/V1.5/data/Depth.json
public sealed class OcclusionFlag { }     // OSD/V1.5/data/OcclusionFlag.json
public sealed class InteractionPotential { } // OSD/V1.5/data/InteractionPotential.json
public sealed class SalienceScore { }     // OSD/V1.5/data/SalienceScore.json

  // ---------------------------------------------------------------------------
  //  3D Model Object - OSD/V1.5/data/3DModelObject.json (OSD-3DO). The composite:
  //  a 3D Model Object aggregates Basic 3D Model Objects (leaves, OSD-B3O) and/or
  //  child 3D Model Objects (recursive). Replaces the empty `ThreeDModelObject { }`
  //  placeholder previously here (marked "not yet provided" - now provided). The
  //  Basic/leaf object lives in Mpai.Core with the other Basic objects, referenced
  //  fully-qualified; the placement rule (objects in OSD) predates this file and is
  //  not retrofitted, so existing references are left undisturbed.
  // ---------------------------------------------------------------------------
  public sealed class ThreeDModelObject
  {
      public string Header { get; init; } = "OSD-3DO-V1.5";
      public string? MInstanceID { get; init; }
      public string? UEnvironmentID { get; init; }
      public string ThreeDModelObjectID { get; init; } = "";

      public System.Collections.Generic.List<Basic3DModelObject>? Basic3DModelObjects { get; init; }
      public System.Collections.Generic.List<ThreeDModelObject>? Sub3DModelObjects { get; init; }

      public string? DescrMetadata { get; init; }
  }

// ---------------------------------------------------------------------------
//  Audio Source - OSD/V1.5/data/AudioSource.json
//  Characterizes a physical sound source AOA acquires from - ties to the
//  CAE-ASM spec's "Source Characteristics" / GetSourceCharacteristics.
// ---------------------------------------------------------------------------
public sealed class AudioSource
{
    public string Header { get; init; } = "OSD-AUS-V1.5";   // schema's Header pattern has the same typo as SimpleTime's
    public string MInstanceID { get; init; } = "";
    public string? UEnvironmentID { get; init; }
    public string AudioSourceID { get; init; } = "";

    public SpaceTime? AudioSourceSpaceTime { get; init; }

    // Distinct from AcousticProfile.FrequencyRange: this describes the
    // source's general capability (e.g. an instrument's playable range),
    // not the measured spectral content of one specific captured signal.
    public FrequencyRange? FrequencyRange { get; init; }

    // Moved here from AcousticProfile per schema update - Timbre (attack/
    // overtones/resonance) is intrinsic to the emitting source, not the room.
    public Timbre? Timbre { get; init; }

    public List<AudioSourceTypeEntry>? AudioSourceType { get; init; }

    public DataExchangeMetadata? DataXMData { get; init; }
    public string? DescrMetadata { get; init; }
}

// AudioSourceType is a oneOf per entry: Diffuseness | DirectionalPatterns | SizeAndShape.
// Modelled as one class with all three nullable rather than a discriminated
// union, consistent with how BasicAudioObjectData's simpler anyOf was handled
// with real record types - this one is kept simpler since exactly one of the
// three is expected to be set per entry, not parsed from raw JSON here.
public sealed class AudioSourceTypeEntry
{
    public Plot? Diffuseness { get; init; }
    public Plot? DirectionalPatterns { get; init; }
    public SizeAndShapeEntry? SizeAndShape { get; init; }
}

public sealed class SizeAndShapeEntry
{
    public Plot? FreqDirInt { get; init; }
    // Renamed from the schema's literal "SizeAndShape" (same name as its own
    // containing property) for the same reason BasicAudioSceneDescriptors'
    // entries list was renamed - avoids a property sharing its container's name.
    public ThreeDModelObject? Shape { get; init; }
}

// ---------------------------------------------------------------------------
//  Audio Object - OSD/V1.5/data/AudioObject.json
//  The composite: leaf-or-composite, unlimited nesting depth via
//  BasicAudioObjects (leaves) and/or SubAudioObjects (recursive children).
// ---------------------------------------------------------------------------
public sealed class AudioObject
{
    public string Header { get; init; } = "OSD-AUO-V1.5";
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string AudioObjectID { get; init; } = "";
    public SimpleTime? AudioObjectTime { get; init; }
    public SpaceTime? AudioObjectSpaceTime { get; init; }   // fixed: confirmed SpaceTime by the corrected schema

    // OSD/V1.5/data/AudioObject.json carries this and the class did not - the
    // same gap BasicAudioObject had. Without it a composed Object has nowhere to
    // record where it is being listened FROM.
    public PointOfView? UserPoV { get; init; }

    public AcousticProfile? AudioObjectProperties { get; init; }

    public List<string>? ParentAudioObjectIDs { get; init; }

    public int? BasicAudioObjectCount { get; init; }
    public List<BasicAudioObjectEntry>? BasicAudioObjects { get; init; }

    public int? SubAudioObjectCount { get; init; }
    public List<SubAudioObjectEntry>? SubAudioObjects { get; init; }

    public DataExchangeMetadata? DataXMData { get; init; }
    public string? DescrMetadata { get; init; }
}

public sealed class BasicAudioObjectEntry
{
    public SpaceTime? BasicAudioObjectSpaceTime { get; init; }   // fixed: confirmed SpaceTime by the corrected schema
    public BasicAudioObject? BAObjectIDOrBAObject { get; init; }   // object or id-string (simplified to object)
}

public sealed class SubAudioObjectEntry
{
    public SpaceTime? SubAudioObjectSpaceTime { get; init; }   // fixed: confirmed SpaceTime by the corrected schema
    public AudioObject? SubAObjectIDOrSubAObject { get; init; }   // object or id-string (simplified to object)
}

// ---------------------------------------------------------------------------
//  Basic Audio Scene Descriptors - OSD/V1.5/data/BasicAudioSceneDescriptors.json
//  A flat-ish scene: each entry places either a BasicAudioObject directly
//  (post-correction) or its id, with a PointOfView per entry.
// ---------------------------------------------------------------------------
public sealed class BasicAudioSceneDescriptors
{
    public string Header { get; init; } = "OSD-BAD-V1.5";
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string BasicAudioSceneDescriptorsID { get; init; } = "";
    public SimpleTime? BasicAudioSceneDescriptorsTime { get; init; }

    public string? ParentBASID { get; init; }

    public SpaceTime? BASSpaceTime { get; init; }
    public PointOfView? ListenerPointOfView { get; init; }
    public double? GravityValue { get; init; }

    public int AudioObjectCount { get; init; }
    // Named "...Entries" rather than the schema's literal "BasicAudioSceneDescriptors"
    // to avoid a property sharing the exact name of its own containing class.
    public List<BasicAudioSceneEntry> BasicAudioSceneDescriptorsEntries { get; init; } = new();

    public DataExchangeMetadata? DataXMData { get; init; }
    public string? DescrMetadata { get; init; }
}

public sealed class BasicAudioSceneEntry
{
    public SpaceTime? AudioObjectSpaceTime { get; init; }
    public BasicAudioObject? AudioObjectIDOrAudioObject { get; init; }   // object or id-string (simplified to object)
    public List<AudioSceneEnrichment>? AudioSceneEnrichment { get; init; }
    public PointOfView PointOfView { get; init; } = new();
}

public sealed class AudioSceneEnrichment
{
    public Trace? EnrichmentTrace { get; init; }
    public Depth? Depth { get; init; }
    public OcclusionFlag? OcclusionFlag { get; init; }
    public InteractionPotential? InteractionPotential { get; init; }
    public SalienceScore? SalienceScore { get; init; }
}

// ---------------------------------------------------------------------------
//  Audio Scene Descriptors - OSD/V1.5/data/AudioSceneDescriptors.json
//  The full/hierarchical scene: AudioObjects plus recursive SubAudioScenes.
// ---------------------------------------------------------------------------
public sealed class AudioSceneDescriptors
{
    public string Header { get; init; } = "OSD-ASD-V1.5";
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string AudioSceneDescriptorsID { get; init; } = "";
    public SimpleTime? AudioSceneDescriptorsTime { get; init; }
    public SimpleTime? AudioSceneDescriptorsSpaceTime { get; init; }   // schema refs SimpleTime here too - kept faithful

    // Confirmed by the corrected schema: unlike BasicAudioSceneDescriptors,
    // this field was genuinely missing before, not just unimplemented - now
    // real. This is what lets a scene's listener position actually persist
    // (e.g. dragged on the canvas) rather than being a delivery-time-only
    // value that resets every session.
    public PointOfView? ListenerPointOfView { get; init; }

    public int? AudioObjectCount { get; init; }
    public List<AudioSceneObjectEntry>? AudioObjects { get; init; }

    public int? SubAudioSceneCount { get; init; }
    public List<SubAudioSceneEntry>? SubAudioScenes { get; init; }

    public DataExchangeMetadata? DataXMData { get; init; }
    public string? DescrMetadata { get; init; }
}

public sealed class AudioSceneObjectEntry
{
    public SpaceTime? AudioObjectSpaceTime { get; init; }   // fixed: schema's $ref corrected from SimpleTime to SpaceTime
    public AudioObject? ObjectIDOrObject { get; init; }   // object or id-string (simplified to object)
}

public sealed class SubAudioSceneEntry
{
    public SimpleTime? SubAudioSceneSpaceTime { get; init; }
    public AudioSceneDescriptors? SubAudioSceneIDOrSubAudioScene { get; init; }   // object or id-string (simplified to object); recursive
}

// ---------------------------------------------------------------------------
//  Audio Event Descriptors - OSD/V1.5/data/AudioEventDescriptors.json
//
//  Field naming (MInstanceID/UEnvironmentID) and the "Trace" required-but-
//  undefined issue flagged on first pass are both now resolved upstream in
//  the schema (MetaverseID/UEnvironment renamed to match every other type's
//  convention; Trace removed from `required` rather than defined) - nothing
//  further needed here.
// ---------------------------------------------------------------------------
public sealed class AudioEventDescriptors
{
    public string Header { get; init; } = "OSD-AED-V1.5";   // schema's Header pattern has the same typo as SimpleTime's/AudioSource's
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string AudioEventDescriptorsID { get; init; } = "";
    public SimpleTime? AudioEventDescriptorsTime { get; init; }
    public SpaceTime? AudioEventDescriptorsSpaceTime { get; init; }

    // Named "...Entries" rather than the schema's literal "AudioEventDescriptors"
    // to avoid a property sharing the exact name of its own containing class -
    // same reasoning as BasicAudioSceneDescriptorsEntries.
    public List<AudioEventEntry>? AudioEventDescriptorsEntries { get; init; }

    public DataExchangeMetadata? DataXMData { get; init; }
    public string? DescrMetadata { get; init; }
}

public sealed class AudioEventEntry
{
    // Embedded Event Descriptors (recursive) or an Event ID (by reference) -
    // object or id-string (simplified to object), same oneOf-simplification
    // convention used throughout.
    public AudioEventDescriptors? ASDIDOrAudioASD { get; init; }   // corrected to match the real schema's field name
}