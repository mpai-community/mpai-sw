namespace Mpai.Core;

// ---------------------------------------------------------------------------
//  Qualifier types, projecting the TFA/V1.5 Qualifier schemas. A Qualifier is
//  the running description of an object: it is inherited in part from the
//  input and determined in part by the producing AIM.
// ---------------------------------------------------------------------------

// TFA/V1.5/data/TextQualifier.json
public sealed class TextQualifier
{
    public string Header { get; init; } = "TFA-TXQ-V1.5";
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string TextQualifierID { get; init; } = "";
    public SpaceTime? TextQualifierTime { get; init; }

    public SubType? SubType { get; init; }
    public TextFormat? Format { get; init; }
    public TextAttributes? Attributes { get; init; }

    public DataExchangeMetadata? DataXMData { get; init; }
    public string? DescrMetadata { get; init; }
}

public sealed class TextFormat
{
    public TextContentFormat? ContentFormat { get; init; }
}

public sealed class TextContentFormat
{
    public string? Static { get; init; }    // TextStaticFormat.*
    public object? Dynamic { get; init; }    // TFA TextDynamicFormats.json (not yet provided)
}

public sealed class TextAttributes
{
    public InstanceIdentifier? ObjectIdentifier { get; init; }
    public Language? Language { get; init; }
}

// Shared by TextQualifier and SpeechQualifier (same shape in both schemas).
public sealed class Language
{
    public string? LanguageCode { get; init; }
    public string? LanguageFormat { get; init; }   // LanguageFormat.*
}

// SubType is an open object in the schemas; kept as an extensible placeholder.
public sealed class SubType { }

// TFA/V1.5/data/AudioQualifier.json â€” schema not yet provided. Reuses the
// same Format/Attributes shape as SpeechQualifier (PCM, file format, and
// device metadata aren't inherently speech-specific) rather than an empty
// placeholder that would discard real backend-determined data. Revisit the
// internals here once the real schema arrives; BasicAudioObject's use of
// this type does not need to change.
// TFA/V1.5/data/AudioQualifier.json
//
// AUDIO'S OWN, no longer speech's. This held a SpeechFormat and a
// SpeechAttributes - types named for one medium and used by another, so an Audio
// Object was described in speech terms and nothing meaningful could be recorded
// about it. Sharing a Data Type is ordinary; BORROWING one named for something
// else is not: language belongs to speech and to text, and neither takes it from
// the other.
//
// THE OUTER SHAPE IS COMPLETE and the interiors are filled as far as anything
// reaches. Ambisonics, spherical harmonics, binaural and spectral cues,
// microphone array geometry and directivity are named in the schema and left as
// gaps here - so six degrees of freedom, when it arrives, means FILLING
// RawData.Ambisonics rather than rearranging what sits above it.
public sealed class AudioQualifier
{
    public string Header { get; init; } = "TFA-AUQ-V1.5";
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string AudioQualifierID { get; init; } = "";

    // WHEN THE QUALIFIER WAS MADE - a SimpleTime, as the schema says, not the
    // SpaceTime this once was. It is not the duration of the audio: that belongs
    // to the Object's own SpaceTime, where the window's t (s) fields point.
    public SimpleTime? AudioQualifierTime { get; init; }

    // Speech | Music | SoundEffects | Noise | Mixed.
    //
    // Left unset by acquisition, which cannot know: a WAV header says nothing
    // about what was recorded. A default of "Mixed" would be a claim rather than
    // a fact.
    public string? SubTypes { get; init; }

    public AudioFormats? Formats { get; init; }
    public AudioAttributes? Attributes { get; init; }

    public DataExchangeMetadata? DataXMData { get; init; }
    public string? DescrMetadata { get; init; }
}

public sealed class AudioFormats
{
    public AudioContentFormat? ContentFormat { get; init; }
    public AudioTransportFormat? TransportFormat { get; init; }
}

public sealed class AudioContentFormat
{
    public AudioRawData? RawData { get; init; }

    // TFA/V1.5/formats/AudioContentFormats.json - AAC-LC, FLAC, MP3, WAV and the
    // rest.
    public string? OtherContentFormats { get; init; }
}

public sealed class AudioRawData
{
    // TFA/V1.5/formats/PCM.json - where a WAV header's sampling frequency and
    // precision go.
    //
    // The existing Pcm in Formats.cs, which models the schema AS IT WAS: a list
    // of PcmChannel, from when PCM was declared an array. The schema has been
    // corrected to an object, but four files build PcmChannel today - Piper and
    // all three acquisitions - so the type follows when they do, not before.
    public Pcm? SampleSpace { get; init; }

    // TransformSpace, SpherlHarmDecompos, Ambisonics and MultiPointAmbisonics
    // are in the schema and not yet needed. They belong here when they are.
}

public sealed class AudioTransportFormat
{
    // TFA/V1.5/formats/AudioFileFormats.json - WAV, FLAC, MP4 and the rest.
    public string? FileFormats { get; init; }

    // TFA/V1.5/formats/AudioStreamFormats.json
    public string? StreamFormats { get; init; }
}

public sealed class AudioAttributes
{
    public string? Source { get; init; }
    public AcousticProfile? AcousticProfile { get; init; }
    public AudioDevice? Device { get; init; }

    // Metadata and SpatialAttributes - binaural cues, spectral cues, interchannel
    // differences - are in the schema and not yet needed.
}

// The values TFA/V1.5/formats/AudioFileFormats.json and AudioContentFormats.json
// enumerate, as constants rather than magic strings at the point of use.
public static class AudioFileFormat
{
    public const string Wav  = "WAV";
    public const string Flac = "FLAC";
    public const string Mp3  = "MP3";
    public const string Mp4  = "MP4";
    public const string Ogg  = "OGG";
    public const string Opus = "Opus";
    public const string Aac  = "AAC";
    public const string Aiff = "AIFF";
    public const string Alac = "ALAC";
}

public static class AudioSubType
{
    public const string Speech       = "Speech";
    public const string Music        = "Music";
    public const string SoundEffects = "SoundEffects";
    public const string Noise        = "Noise";
    public const string Mixed        = "Mixed";
}

// TFA/V1.5/data/SpeechQualifier.json
public sealed class SpeechQualifier
{
    public string Header { get; init; } = "TFA-SPQ-V1.5";
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string SpeechQualifierID { get; init; } = "";
    public SpaceTime? SpeechQualifierTime { get; init; }

    public SubType? SubType { get; init; }
    public SpeechFormat? Format { get; init; }
    public SpeechAttributes? Attributes { get; init; }

    public DataExchangeMetadata? DataXMData { get; init; }
    public string? DescrMetadata { get; init; }
}

public sealed class SpeechFormat
{
    public SpeechContentFormats? ContentFormats { get; init; }
    public SpeechTransportFormats? TransportFormats { get; init; }
}

public sealed class SpeechContentFormats
{
    public Pcm? RawData { get; init; }
    public object? OtherContentFormats { get; init; }   // TFA SpeechContentFormats.json (not yet provided)
}

public sealed class SpeechTransportFormats
{
    public string? FileFormat { get; init; }    // SpeechFileFormat.*
    public object? StreamFormat { get; init; }   // TFA SpeechStreamFormats.json (not yet provided)
}

public sealed class SpeechAttributes
{
    public string? Source { get; init; }         // SpeechSource.*  (Real | Synthetic)
    public SpeechMetadata? Metadata { get; init; }
    public SpeechCharacteristics? SpeechCharacteristics { get; init; }
    public SpeechStructure? Structure { get; init; }
    public AudioDevice? Device { get; init; }
}

public sealed class SpeechMetadata
{
    public Language? Language { get; init; }
    public InstanceIdentifier? SpeakerIdentity { get; init; }
    public SpeakerProperties? SpeakerProperties { get; init; }
    public ContentDescription? ContentDescription { get; init; }
}

public sealed class SpeakerProperties
{
    public string? SpeakerType { get; init; }    // SpeakerType.*  (Human | Agent | Unknown)
    public int? SpeakerCount { get; init; }
}

public sealed class ContentDescription
{
    public TextObject? TextObject { get; init; }          // OSD TextObject (the spoken text, for provenance)
    public PersonalStatus? EntityInternalStatus { get; init; }
}

// Following blocks are populated by the Speech-Characteristics / Capture / Render
// AIMs (later slices); projected here to keep SpeechQualifier faithful.
public sealed class SpeechCharacteristics
{
    public ValueUnit? SpeakingRate { get; init; }   // Unit: WordsPerSecond | SyllablesPerSecond
    public ValueUnit? PitchRange { get; init; }     // Unit: Hertz | Semitones
    public ValueType? Energy { get; init; }         // Type: RMS | Peak | LUFS
    public string? Prosody { get; init; }           // Neutral | Expressive | Emphatic | Monotonic | Other
    public bool? Disfluencies { get; init; }
}

public sealed class ValueUnit { public double? Value { get; init; } public string? Unit { get; init; } }
public sealed class ValueType { public double? Value { get; init; } public string? Type { get; init; } }

public sealed class SpeechStructure
{
    public int? UtteranceCount { get; init; }
    public bool? TurnBased { get; init; }
}

public sealed class AudioDevice
{
    public string? DeviceID { get; init; }
    public string? DeviceRole { get; init; }    // Capture | Render | Bidirectional
    public string? DeviceType { get; init; }    // Microphone | Speaker | ...
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public CaptureConfiguration? CaptureConfiguration { get; init; }
    public RenderConfiguration? RenderConfiguration { get; init; }
    public Synchronisation? Synchronisation { get; init; }
    // DeviceLocation / DeviceGeometry / MicrophoneDirectivityFormat refs: not yet provided.
}

public sealed class CaptureConfiguration { public int? ChannelCount { get; init; } public string? SamplingMode { get; init; } }
public sealed class RenderConfiguration  { public int? ChannelCount { get; init; } public string? RenderingMode { get; init; } }
public sealed class Synchronisation      { public string? ClockType { get; init; } public string? Reference { get; init; } }