using System.Collections.Generic;

using Mpai.Core;   // PointOfView, SpaceTime, SimpleTime, DataExchangeMetadata

namespace Mpai.Aims.Audio;

// =============================================================================
//  Microphone Array Geometry  (CAE-MAG-V2.5)
//  Maps schemas.mpai.community/CAE1/V2.5/data/MicrophoneArrayGeometry.json
//
//  Carries the array-level configuration and the per-microphone PointOfView that
//  the AVS audio pipeline needs to compute Direction-of-Arrival. This is an
//  EXISTING MPAI type (CAE namespace), referenced here rather than invented.
//
//  Cross-standard note: reuses TFA (MicrophoneArrayTypes, PCM,
//  MicrophoneDirectivityFormats) and OSD (PointOfView, SpaceTime, SimpleTime).
//  Types marked TODO below resolve to existing schema types once their C#
//  counterparts are referenced; string/placeholder stand-ins are used where the
//  repo does not yet expose them, so this compiles as a skeleton.
// =============================================================================
public sealed class MicrophoneArrayGeometry
{
    public string Header { get; init; } = "CAE-MAG-V2.5";        // const per schema
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string MicrophoneArrayGeometryID { get; init; } = "";

    public SimpleTime? MicrophoneArrayTime { get; init; }        // OSD SimpleTime
    public SpaceTime? MicrophoneArraySpaceTime { get; init; }    // OSD SpaceTime (required by schema)

    public required MicrophoneArrayAttributes MicrophoneArrayAttributes { get; init; }
    public required List<MicrophoneAttributesItem> MicrophoneAttributes { get; init; }

    public DataExchangeMetadata? DataXMData { get; init; }       // PTF
    public string? DescrMetadata { get; init; }                  // maxLength 2048 in schema
}

// ---- Array-level configuration ---------------------------------------------
public sealed class MicrophoneArrayAttributes
{
    // TFA MicrophoneArrayTypes (e.g. Linear, Circular, Planar). String stand-in
    // until the TFA enum type is referenced.
    public required string ArrayType { get; init; }

    public int? ArrayScat { get; init; }                         // minimum 0
    public string? ArrayFilterURI { get; init; }                 // uri

    // TFA PCM (sample rate, bit depth, ...). String/object stand-in for now.
    public required object SamplingParameters { get; init; }

    public int? BlockSize { get; init; }                         // minimum 1
    public required int NumberofMicrophones { get; init; }       // minimum 1
}

// ---- Per-microphone entry ---------------------------------------------------
public sealed class MicrophoneAttributesItem
{
    public required int MicrophoneID { get; init; }              // minimum 0

    // TFA MicrophoneDirectivityFormats. String stand-in until referenced.
    public required string MicrophoneDirectivityFormat { get; init; }

    // OSD PointOfView â€” position AND orientation of THIS microphone. This is the
    // inter-microphone geometry DOA consumes.
    public required PointOfView MicrophonePointOfView { get; init; }
}
