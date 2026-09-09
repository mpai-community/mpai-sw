using System;
using System.Collections.Generic;
using System.Linq;

namespace Mpai.Core;

// ---------------------------------------------------------------------------
//  Placeholder projections for schemas not yet shared. Shapes are stubbed so
//  the surrounding types are structurally correct; fill them in when the
//  referenced schemas arrive.
// ---------------------------------------------------------------------------
// OSD/V1.5/data/SpaceTime.json
public sealed class SpaceTime
{
    public string Header { get; init; } = "OSD-SPT-V1.5";
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string SpaceTimeID { get; init; } = "";
    public SimpleTime? SpaceTimeTime { get; init; }

    public SpatialAttitude? SpatialAttitude1 { get; init; }   // at T0
    public SpatialAttitude? SpatialAttitude2 { get; init; }   // at T1
    public SimpleTime Time { get; init; } = new();             // interval between T0 and T1

    public DataExchangeMetadata? DataXMData { get; init; }
    public string? DescrMetadata { get; init; }
}

// (SpatialAttitude is defined properly further below, replacing this placeholder)
// OSD/V1.5/data/SimpleTime.json
// Header pattern in the schema as given has a typo (spurious commas, missing
// "V"): "^OSD-STM-[0-9],{1,2},[.][0-9],{1,2}$". Using the convention every
// other Header follows instead ("OSD-STM-V1.5"), consistent with SpaceTime,
// AudioObject, etc.
public sealed class SimpleTime
{
    public string Header { get; init; } = "OSD-STM-V1.5";
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string SimpleTimeID { get; init; } = "";
    public List<TimeSegment> SimpleTimeData { get; init; } = new();
    public string? DescrMetadata { get; init; }
}

// One time segment. FlagsByte and the decoded TimeType/TimeUnit/Reserved are
// modelled as separate properties, exactly as the schema lists them, even
// though the description explains them as "decoded from FlagsByte" - the
// schema does not actually compute one from the other, both are present.
public sealed class TimeSegment
{
    public int FlagsByte { get; init; }
    public double StartTime { get; init; }
    public double EndTime { get; init; }

    public string AccuracyMode { get; init; } = "single";   // "single" | "separate"
    public double? AccuracyPlusMinus { get; init; }         // required when AccuracyMode == "single"
    public double? AccuracyStartPlusMinus { get; init; }    // required when AccuracyMode == "separate"
    public double? AccuracyEndPlusMinus { get; init; }      // required when AccuracyMode == "separate"

    public bool? TimeType { get; init; }    // false=Relative (epoch 0000-00-00T00:00), true=Absolute (epoch 1970-01-01T00:00)
    public string? TimeUnit { get; init; }  // "00"=sec, "01"=ms, "10"=us, "11"=ns
    public int? Reserved { get; init; }     // 0-31
}

// OSD/V1.5/data/PointOfView.json
public sealed class PointOfView
{
    public string Header { get; init; } = "OSD-OPV-V1.5";
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string PointOfViewID { get; init; } = "";
    public SimpleTime? PointOfViewTime { get; init; }

    public GeneralClassification? General { get; init; }

    public double[] CartPosition { get; init; } = new double[3];   // required: (X, Y, Z) metres
    public double[]? CartAccuracy { get; init; }                    // (X, Y, Z) metres
    public double[]? SpherPosition { get; init; }                   // (r, phi, theta) metres/degrees
    public double[]? SpherAccuracy { get; init; }
    public double[] Orientation { get; init; } = new double[3];     // required: Euler angles (alpha, beta, gamma) degrees
    public double[]? OrientAccuracy { get; init; }

    public List<ApertureEntry>? Aperture { get; init; }
    public double? FocalDistance { get; init; }

    public DataExchangeMetadata? DataXMData { get; init; }
    public string? DescrMetadata { get; init; }
}

// Shared by PointOfView.General and SpatialAttitude.General. PointOfView
// requires all three fields; SpatialAttitude only requires CoordType - kept
// as one class with ObjectType/MediaType nullable (a strict superset of
// both), rather than two near-identical classes.
public sealed class GeneralClassification
{
    public CoordinateType CoordType { get; init; }
    public ObjectType? ObjectType { get; init; }
    public string? MediaType { get; init; }   // MediaType.* constant
}

public sealed class ApertureEntry
{
    public double Azimuth { get; init; }
    public double Elevation { get; init; }
}

// TFA/V1.5/types/CoordinateTypes.json and ObjectTypes.json - real schemas.
public enum CoordinateType { Cartesian, Spherical, Cylindrical, Geodetic, Toroidal }
public enum ObjectType { DigitalHuman, Generic }

// TFA/V1.5/types/MediaTypes.json - real schema. Kept as string constants
// (matching the Formats.cs convention) rather than a C# enum, because two of
// its wire values ("3D Model", "OfflineMap" - note the inconsistent spacing
// between them) aren't valid C# enum member names.
public static class MediaType
{
    public const string ThreeDModel = "3D Model";
    public const string Audio = "Audio";
    public const string AudioVisual = "AudioVisual";
    public const string Haptic = "Haptic";
    public const string LiDAR = "LiDAR";
    public const string OfflineMap = "OfflineMap";
    public const string RADAR = "RADAR";
    public const string Smell = "Smell";
    public const string Speech = "Speech";
    public const string Text = "Text";
    public const string Ultrasound = "Ultrasound";
    public const string Visual = "Visual";
}

// OSD/V1.5/data/SpatialAttitude.json
public sealed class SpatialAttitude
{
    public string Header { get; init; } = "OSD-OSA-V1.5";
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string ObjectSpatialAttitudeID { get; init; } = "";
    public SimpleTime? SpatialAttitudeTime { get; init; }

    public GeneralClassification? General { get; init; }

    public Position Position { get; init; } = new();       // required
    public Orientation Orientation { get; init; } = new();  // required

    public DataExchangeMetadata? DataXMData { get; init; }
    public string? DescrMetadata { get; init; }
}

// OSD/V1.5/data/Position.json
public sealed class Position
{
    public string Header { get; init; } = "OSD-OPS-V1.5";
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string PositionID { get; init; } = "";
    public SimpleTime? PositionTime { get; init; }
    public SimpleTime? PositionSpaceTime { get; init; }   // schema refs SimpleTime here (field named "...SpaceTime") - not confirmed as the same bug as AudioObject's; kept faithful until confirmed

    public GeneralClassification? General { get; init; }   // General.CoordType is required here, matching GeneralClassification's shape exactly

    public double[]? CartPosition { get; init; }
    public double[]? CartPositionAccuracy { get; init; }
    public double[]? SpherPosition { get; init; }
    public double[]? SpherPositionAccuracy { get; init; }

    public double[]? CartVelocity { get; init; }
    public double[]? CartVelocityAccuracy { get; init; }
    public double[]? SpherVelocity { get; init; }
    public double[]? SpherVelocityAccuracy { get; init; }

    public double[]? CartAccel { get; init; }
    public double[]? CartAccelAccuracy { get; init; }
    public double[]? SpherAccel { get; init; }
    public double[]? SpherAccelAccuracy { get; init; }

    public DataExchangeMetadata? DataXMData { get; init; }
    public string? DescrMetadata { get; init; }
}

// OSD/V1.5/data/Orientation.json
public sealed class Orientation
{
    public string Header { get; init; } = "OSD-OOR-V1.5";
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string OrientationID { get; init; } = "";
    public SimpleTime? OrientationTime { get; init; }
    public SimpleTime? OrientationSpaceTime { get; init; }   // schema refs SimpleTime here too - same caveat as Position's PositionSpaceTime

    public OrientationGeneral? General { get; init; }   // no CoordType here (unlike Position/PointOfView/SpatialAttitude) - orientation isn't coordinate-system dependent

    // Named "EulerAngles" rather than the schema's literal "Orientation" to
    // avoid a property sharing the exact name of its own containing class -
    // same reasoning as BasicAudioSceneDescriptorsEntries.
    public double[] EulerAngles { get; init; } = new double[3];   // required: (alpha, beta, gamma) degrees
    public double[]? OrientAccuracy { get; init; }
    public double[]? OrientVelocity { get; init; }
    public double[]? OrientVelocityAccuracy { get; init; }
    public double[]? OrientAccel { get; init; }
    public double[]? OrientAccelAccuracy { get; init; }

    public DataExchangeMetadata? DataXMData { get; init; }
    public string? DescrMetadata { get; init; }
}

public sealed class OrientationGeneral
{
    public ObjectType? ObjectType { get; init; }
    public string? MediaType { get; init; }   // MediaType.* constant
}
public sealed class DataExchangeMetadata { }      // PTF/V1.0/data/DataExchangeMetadata.json
public sealed class PersonalStatus { }            // MMC/V2.5/data/PersonalStatus.json
// VisualQualifier is now defined in VisualQualifier.cs (TFA/V1.5 schema).

// ---------------------------------------------------------------------------
//  Basic Text Object ÃƒÂ¢Ã¢â€šÂ¬Ã¯Â¿Â½?the ATOMIC unit: Basic Text Data + a Text Qualifier.
//  OSD/V1.5/data/BasicTextObject.json
// ---------------------------------------------------------------------------
public sealed class BasicTextObject
{
    public string Header { get; init; } = "OSD-BTO-V1.5";
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string BasicTextObjectID { get; init; } = "";
    public SpaceTime? BasicTextObjectSpaceTime { get; init; }

    public List<BasicTextDataItem> BasicTextData { get; init; } = new();
    public TextQualifier? TextQualifier { get; init; }

    public DataExchangeMetadata? DataXMData { get; init; }
    public string? DescrMetadata { get; init; }

    // Convenience: the inline text (concatenated inline data items).
    public string GetText() =>
        string.Concat(BasicTextData.OfType<InlineTextData>().Select(d => d.Data));

    public static BasicTextObject FromText(string text, TextQualifier? qualifier = null) => new()
    {
        BasicTextObjectID = Guid.NewGuid().ToString(),
        BasicTextData = new() { new InlineTextData(text) },
        TextQualifier = qualifier
    };
}

// BasicTextData is a oneOf: inline data | length+URI reference | id reference.
public abstract record BasicTextDataItem;
public sealed record InlineTextData(string Data) : BasicTextDataItem;
public sealed record ReferencedData(long Length, string DataURI) : BasicTextDataItem;
public sealed record IdentifiedData(string ID) : BasicTextDataItem;

// ---------------------------------------------------------------------------
//  Basic Speech Object ÃƒÂ¢Ã¢â€šÂ¬Ã¯Â¿Â½?atomic speech unit (Data + Speech Qualifier).
//  Projected by analogy to BasicTextObject; the audio schema is not yet shared.
// ---------------------------------------------------------------------------
public sealed class BasicSpeechObject
{
    // OSD-BSO-V1.5, not OSD-BAO. Every Speech Object this codebase created
    // declared itself an AUDIO Object - one string, marked as a placeholder and
    // never replaced, and the purest form of the confusion between the two.
    public string Header { get; init; } = "OSD-BSO-V1.5";
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string BasicSpeechObjectID { get; init; } = "";
    public SpaceTime? BasicSpeechObjectSpaceTime { get; init; }

    public byte[] Data { get; init; } = [];                    // inline speech data (e.g. WAV/PCM)
    public SpeechQualifier? SpeechQualifier { get; init; }

    public DataExchangeMetadata? DataXMData { get; init; }
    public string? DescrMetadata { get; init; }

    public static BasicSpeechObject FromData(byte[] data, SpeechQualifier? qualifier = null) => new()
    {
        BasicSpeechObjectID = Guid.NewGuid().ToString(),
        Data = data,
        SpeechQualifier = qualifier
    };

    // Reinterpret the BYTES as a Basic Audio Object. The Qualifier does not
    // cross: a Speech Qualifier describes speech - its language, its speaker -
    // and an Audio Qualifier describes audio. Neither is the other in different
    // words, which the comment here used to claim ("Audio == Speech at this
    // level"): if that were so, OSD-AUO and OSD-SPO would not be separate Data
    // Types.
    //
    // The receiving AIM should DETERMINE an Audio Qualifier for what it now
    // holds, as MMC-SOA does in the other direction - it asserts a Speech
    // Qualifier and says in its own comments why an assertion is the honest
    // thing and a conversion is not.
    public BasicAudioObject AsAudio() => BasicAudioObject.FromData(Data);
}

// ---------------------------------------------------------------------------
//  Basic Audio Object ÃƒÂ¢Ã¢â€šÂ¬Ã¯Â¿Â½?OSD/V1.5/data/BasicAudioObject.json, schema-correct.
//
//  The stored shape now matches the real schema: BasicAudioObjectData is the
//  array-of-variants the schema specifies (inline/reference/id), not a raw
//  byte array, and AudioQualifier is its own field rather than a
//  borrowed SpeechQualifier.
//
//  .Data and .Qualifier below are a COMPATIBILITY SURFACE, not the real
//  storage: every existing AOA/AOD backend (Alsa/Wasapi/File acquisition,
//  Aplay/File/Winmm delivery) and FromData() reads/writes through them, so
//  none of that code needs to change for this fix. They project to/from the
//  schema-correct fields underneath.
//
//  AudioQualifier is typed as AudioQualifier (TFA/V1.5/data/
//  AudioQualifier.json) ÃƒÂ¢Ã¢â€šÂ¬Ã¯Â¿Â½?a schema not yet provided. Rather than an empty
//  placeholder that would discard the real sample-rate/format/device data
//  every backend already determines, AudioQualifier (see Qualifiers.cs)
//  reuses the same Format/Attributes shape already used for Speech, so no
//  information is lost; only its internals need revisiting once the real
//  schema arrives, not every call site.
// ---------------------------------------------------------------------------
public sealed class BasicAudioObject
{
    public string Header { get; init; } = "OSD-BAO-V1.5";
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string BasicAudioObjectID { get; init; } = "";
    public SpaceTime? BasicAudioObjectTime { get; init; }

    public List<ParentObjectRef>? ParentObjects { get; init; }
    public List<ChildObjectRef>? ChildObjects { get; init; }

    public List<BasicAudioObjectDataItem> BasicAudioObjectData { get; init; } = new();
    // OSD/V1.5/data/BasicAudioObject.json has carried this at top level all
    // along; the class did not, so nothing could store a listener for a single
    // Object and CAE-AOE had nowhere to put one.
    //
    // A lone Basic Object sits at the origin - it is what is being auditioned.
    // What moves is the EAR. When the Object is later placed in a Scene, the
    // Scene's ListenerPointOfView overrides this one, per the rule that an
    // entity keeps its own attributes unless the context provides them.
    public PointOfView? ListenerPointOfView { get; init; }

    public BasicAudioObjectProperties? BasicAudioObjectProperties { get; init; }
    public AudioQualifier? AudioQualifier { get; init; }

    public DataExchangeMetadata? DataXMData { get; init; }
    public string? DescrMetadata { get; init; }

    // --- Compatibility surface (see note above) ---
    public byte[] Data =>
        BasicAudioObjectData.OfType<InlineAudioData>().Select(d => Convert.FromBase64String(d.Data)).FirstOrDefault()
        ?? [];

    // Qualifier - which presented this Object's Audio Qualifier AS a Speech
    // Qualifier - is gone. It could only exist while the two were secretly one
    // type, copying SubType, Format and Attributes across as though the words
    // meant the same thing in both. Read AudioQualifier directly.

    // An AudioQualifier, which is what a Basic AUDIO Object is described by.
    //
    // This took a SpeechQualifier, so every acquisition built one for audio it
    // had captured and the values were then reinterpreted. The two Qualifiers are
    // separate now, each complete in itself, and nothing converts.
    public static BasicAudioObject FromData(byte[] data, AudioQualifier? qualifier = null) => new()
    {
        BasicAudioObjectID = Guid.NewGuid().ToString(),
        BasicAudioObjectData = new() { new InlineAudioData(Convert.ToBase64String(data)) },
        AudioQualifier = qualifier
    };

    // Reinterpret the BYTES as a Basic Speech Object; the Qualifier does not
    // cross, for the reason given on AsAudio.
    //
    // MMC-SOA is the model for what a caller should do instead: it takes the
    // captured bytes and ASSERTS a Speech Qualifier - Source Real, SpeakerType
    // Human, the language inherited from the request - because asserting that
    // sound is speech is what that AIM is FOR. A conversion records nothing
    // about where the sound came from or why anyone should believe it contains
    // speech.
    public BasicSpeechObject AsSpeech() => BasicSpeechObject.FromData(Data);

    // Returns a copy with a different BasicAudioObjectID. Used by AOE to
    // correct the stored object's own ID to match the Repository AssetId at
    // creation time - otherwise the domain payload's self-assigned GUID (from
    // FromData) and the Repository's readable "BAO000001"-style ID diverge,
    // which surfaces confusingly later (e.g. delivered filenames named by a
    // random GUID instead of the Repository ID everything else uses).
    public BasicAudioObject WithId(string id) => new()
    {
        Header = Header,
        MInstanceID = MInstanceID,
        UEnvironmentID = UEnvironmentID,
        BasicAudioObjectID = id,
        BasicAudioObjectTime = BasicAudioObjectTime,
        ParentObjects = ParentObjects,
        ChildObjects = ChildObjects,
        BasicAudioObjectData = BasicAudioObjectData,
        ListenerPointOfView = ListenerPointOfView,
        BasicAudioObjectProperties = BasicAudioObjectProperties,
        AudioQualifier = AudioQualifier,
        DataXMData = DataXMData,
        DescrMetadata = DescrMetadata
    };

    // ListenerPointOfView was added to this class after WithId was written, and
    // WithId did not copy it - so a listener set before storing was silently
    // dropped on the way in. Adding a field to a hand-written copy method means
    // finding every such method; there was one, and it had been missed.
    public BasicAudioObject WithListener(PointOfView? listener) => new()
    {
        Header = Header,
        MInstanceID = MInstanceID,
        UEnvironmentID = UEnvironmentID,
        BasicAudioObjectID = BasicAudioObjectID,
        BasicAudioObjectTime = BasicAudioObjectTime,
        ParentObjects = ParentObjects,
        ChildObjects = ChildObjects,
        BasicAudioObjectData = BasicAudioObjectData,
        ListenerPointOfView = listener,
        BasicAudioObjectProperties = BasicAudioObjectProperties,
        AudioQualifier = AudioQualifier,
        DataXMData = DataXMData,
        DescrMetadata = DescrMetadata
    };
}

// BasicAudioObjectData is an anyOf: inline data | length+URI reference | id reference.
// Polymorphic attributes are required for System.Text.Json to deserialize
// the abstract base back into the correct concrete variant - without them,
// round-tripping through RepositoryAsset.GetData<T>() fails outright
// (caught by an actual end-to-end AOE test, not just a compile check).
[System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "$dataKind")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(InlineAudioData), "inline")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(ReferencedAudioData), "referenced")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(IdentifiedAudioData), "identified")]
public abstract record BasicAudioObjectDataItem;
public sealed record InlineAudioData(string Data) : BasicAudioObjectDataItem;
public sealed record ReferencedAudioData(long DataLength, string DataURI) : BasicAudioObjectDataItem;
public sealed record IdentifiedAudioData(string ID) : BasicAudioObjectDataItem;

public sealed class ParentObjectRef
{
    public string? ParentObjectID { get; init; }
    public SpaceTime? ParentObjectSpaceTime { get; init; }
}

public sealed class ChildObjectRef
{
    public string? ChildObjectID { get; init; }
    public SpaceTime? ChildObjectSpaceTime { get; init; }
}

public sealed class BasicAudioObjectProperties
{
    public SpaceTime? BasicAudioObjectSpaceTime { get; init; }
    public double? Level { get; init; }
    public bool? PerceptStatus { get; init; }
    public AcousticProfile? AcousticProfile { get; init; }
    public InstanceIdentifier? BasicAudioObjectIdentifier { get; init; }
}

// ---------------------------------------------------------------------------
//  Acoustic Profile ÃƒÂ¢Ã¢â€šÂ¬Ã¯Â¿Â½?OSD/V1.5/data/AcousticProfile.json
// ---------------------------------------------------------------------------
public sealed class AcousticProfile
{
    public string Header { get; init; } = "OSD-ACP-V1.5";
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string AcousticProfileID { get; init; } = "";
    public SpaceTime? AcousticProfileTime { get; init; }

    public FrequencyRange FrequencyRange { get; init; } = new();
    public Plot? Spectrogram { get; init; }
    public double Loudness { get; init; }
    public List<Reflectivity>? Reflectivity { get; init; }
    public List<Reverberation>? Reverberation { get; init; }
    public double? Diffusion { get; init; }
    public double? Absorption { get; init; }
    public List<Doppler>? Doppler { get; init; }

    public DataExchangeMetadata? DataXMData { get; init; }
    public string? DescrMetadata { get; init; }
}

public sealed class FrequencyRange { public double MinFrequencyHz { get; init; } public double MaxFrequencyHz { get; init; } }
public sealed class Timbre
{
    public double? Attack { get; init; }
    public List<double>? Overtones { get; init; }
    public List<List<double>>? Resonance { get; init; }
}
public sealed class Reflectivity { public double EarlyReflectionTime { get; init; } public double LateReflectionTime { get; init; } }
public sealed class Reverberation { public Plot? RT60 { get; init; } public Plot? RT30 { get; init; } public Plot? RT20 { get; init; } public double? EDT { get; init; } }
public sealed class Doppler { public double? DirectSoundFactor { get; init; } public double? IndirectSound { get; init; } }
public sealed class Plot { }   // OSD/V1.5/data/Plot.json ÃƒÂ¢Ã¢â€šÂ¬Ã¯Â¿Â½?not yet provided

// ---------------------------------------------------------------------------
//  Basic Visual Object ÃƒÂ¢Ã¢â€šÂ¬Ã¯Â¿Â½?atomic visual unit (Data + Visual Qualifier).
//  Projected by analogy; the visual schema is not yet shared.
// ---------------------------------------------------------------------------
public sealed class BasicVisualObject
{
    public string Header { get; init; } = "OSD-BVO-V1.5";     // placeholder header
    public string BasicVisualObjectID { get; init; } = "";
    public string? FileName { get; init; }
    public byte[] Data { get; init; } = [];
    public VisualQualifier? VisualQualifier { get; init; }

    public static BasicVisualObject FromFile(string fileName, byte[] data, string? visualObjectType = null) => new()
    {
        BasicVisualObjectID = Guid.NewGuid().ToString(),
        FileName = fileName,
        Data = data,
        VisualQualifier = BuildQualifier(fileName, visualObjectType)
    };

    // A Visual Object is Data + Qualifier. Populate at least the 2D static
    // content format from the file name, so downstream AIMs receive the
    // format rather than raw bytes alone.
    private static VisualQualifier? BuildQualifier(string? fileName, string? visualObjectType = null)
    {
        var fmt = VisualFormatDetection.FromExtension(fileName);
        return fmt is null ? null : VisualQualifier.For2DStill(fmt.Value, visualObjectType: visualObjectType);
    }
}

// ---------------------------------------------------------------------------
//  Text Object ÃƒÂ¢Ã¢â€šÂ¬Ã¯Â¿Â½?the recursive COLLECTION (Basic Text Objects + nested Text
//  Objects). A one-element Text Object is exactly a Basic Text Object.
//  OSD/V1.5/data/TextObject.json
// ---------------------------------------------------------------------------
public sealed class TextObject
{
    public string Header { get; init; } = "OSD-TXO-V1.5";
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string TextObjectID { get; init; } = "";
    public SpaceTime? TextObjectSpaceTime { get; init; }

    public int? BasicTextObjectCount { get; init; }
    public List<BasicTextObjectEntry> BasicTextObjects { get; init; } = new();

    public int? SubTextObjectCount { get; init; }
    public List<SubTextObjectEntry> SubTextObjects { get; init; } = new();

    // Wrap a single Basic Text Object as a one-element Text Object (Object subsumes Basic).
    public static TextObject FromBasic(BasicTextObject basic) => new()
    {
        TextObjectID = Guid.NewGuid().ToString(),
        BasicTextObjectCount = 1,
        BasicTextObjects = new() { new BasicTextObjectEntry { BTObjectIDOrBTObject = basic } }
    };
}

public sealed class BasicTextObjectEntry
{
    public SpaceTime? BasicTextObjectSpaceTime { get; init; }
    public BasicTextObject? BTObjectIDOrBTObject { get; init; }   // object or id-string (simplified to object)
}

public sealed class SubTextObjectEntry
{
    public SpaceTime? SubTextObjectSpaceTime { get; init; }
    public TextObject? SubTObjectIDOrSubTObject { get; init; }
}

// Added to AIMs/Core/Objects.cs
//
// SpeechObject: the composed Speech Object, per OSD/V1.5/data/SpeechObject.json.
//
// Only the BASIC half of speech was ever built, which is why speech kept
// borrowing audio's types: there was nothing else for it to be. Every medium has
// a Basic and a full Object - TextObject has existed all along, AudioObject was
// built for CAE-ASM, and this completes the third.
public sealed class SpeechObject
{
    public string Header { get; init; } = "OSD-SPO-V1.5";
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string SpeechObjectID { get; init; } = "";
    public SimpleTime? SpeechObjectTime { get; init; }
    public SpaceTime? SpeechObjectSpaceTime { get; init; }

    // Where the Object is being listened FROM, as AudioObject has.
    public PointOfView? UserPoV { get; init; }

    public List<string>? ParentSpeechObjectIDs { get; init; }

    public int? BasicSpeechObjectCount { get; init; }
    public List<BasicSpeechObjectEntry>? BasicSpeechObjects { get; init; }

    public int? SubSpeechObjectCount { get; init; }
    public List<SubSpeechObjectEntry>? SubSpeechObjects { get; init; }

    public string? DescrMetadata { get; init; }

    // Wrap a single Basic Speech Object as an Object of one.
    public static SpeechObject FromBasic(BasicSpeechObject basic) => new()
    {
        SpeechObjectID = Guid.NewGuid().ToString(),
        BasicSpeechObjectCount = 1,
        BasicSpeechObjects = new() { new BasicSpeechObjectEntry { BSObjectIDOrBSObject = basic } }
    };
}

public sealed class BasicSpeechObjectEntry
{
    // Where this Basic Speech Object sits WITHIN the containing Object. Absent
    // means the containing Object's own Space/Time.
    public SpaceTime? BasicSpeechObjectSpaceTime { get; init; }
    public BasicSpeechObject? BSObjectIDOrBSObject { get; init; }
}

public sealed class SubSpeechObjectEntry
{
    public SpaceTime? SubSpeechObjectSpaceTime { get; init; }
    public SpeechObject? SubSObjectIDOrSubSObject { get; init; }
}
