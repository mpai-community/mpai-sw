using System.Collections.Generic;

namespace Mpai.Core;

// ---------------------------------------------------------------------------
//  Format enumerations, projected as the EXACT wire strings from the schemas.
//  (Kept as string constants so the C# value equals the JSON const verbatim.)
// ---------------------------------------------------------------------------

// TFA/V1.5/formats/SpeechFileFormats.json
public static class SpeechFileFormat
{
    public const string Wav = "WAV";
    public const string Mp4 = "MP4";
}

// TFA/V1.5/formats/LanguageFormats.json
public static class LanguageFormat
{
    public const string Iso639_1  = "ISO 639-1";
    public const string Iso639_2  = "ISO 639-2";
    public const string Iso639_3  = "ISO 639-3";
    public const string Iso639_5  = "ISO 639-5";
    public const string Bcp47     = "BCP 47";
    public const string Glottocode = "Glottocode";
}

// TFA/V1.5/formats/TextStaticFormats.json
public static class TextStaticFormat
{
    public const string Ascii        = "ASCII";
    public const string IsoIec646    = "ISO/IEC 646";
    public const string IsoIec8859_1 = "ISOIEC 8859-1";
    public const string IsoIec8859_2 = "ISOIEC 8859-2";
    public const string IsoIec8859_3 = "ISOIEC 8859-3";
    public const string IsoIec8859_4 = "ISOIEC 8859-4";
    public const string IsoIec8859_5 = "ISOIEC 8859-5";
    public const string IsoIec8859_6 = "ISOIEC 8859-6";
    public const string IsoIec8859_7 = "ISOIEC 8859-7";
    public const string IsoIec8859_8 = "ISOIEC 8859-8";
    public const string IsoIec8859_9 = "ISOIEC 8859-9";
    public const string IsoIec8859_10 = "ISOIEC 8859-10";
    public const string IsoIec8859_11 = "ISOIEC 8859-11";
    public const string IsoIec8859_12 = "ISOIEC 8859-12";
    public const string IsoIec8859_13 = "ISOIEC 8859-13";
    public const string IsoIec8859_14 = "ISOIEC 8859-14";
    public const string IsoIec8859_15 = "ISOIEC 8859-15";
    public const string IsoIec8859_16 = "ISOIEC 8859-16";
    public const string Utf8  = "UTF-8";
    public const string Utf16 = "UTF-16";
    public const string Utf32 = "UTF-32";
}

// SpeechQualifier Attributes.Source enum
public static class SpeechSource
{
    public const string Real      = "Real";
    public const string Synthetic = "Synthetic";
}

// SpeechQualifier SpeakerProperties.SpeakerType enum
public static class SpeakerType
{
    public const string Human   = "Human";
    public const string Agent   = "Agent";
    public const string Unknown = "Unknown";
}

// TFA/V1.5/formats/PCM.json
//
// The schema declared PCM as an ARRAY of objects, which was a drafting artefact:
// it meant "these three values are contained", not "several sets of them". The
// schema has been corrected to an object and the class follows, so PcmChannel is
// gone and its three fields sit here.
public sealed class Pcm
{
    public string Header { get; init; } = "TFA-PCM-V1.5";

    public double? SamplingFrequency { get; init; }

    // THE BITS USED TO REPRESENT A SAMPLE. Every caller wrote the bit depth into
    // SamplePrecision, which is not what it is for - a consistent habit, and
    // consistently wrong.
    public int? Precision { get; init; }

    // In the schema, and its meaning no longer known. Kept so the type matches
    // its schema, and deliberately never written: inventing a value would be
    // worse than an empty optional field.
    public double? SamplePrecision { get; init; }
}
