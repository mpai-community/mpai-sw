using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

using Mpai.Core;
using Mmc.Tts;

namespace Mpai.Aims.Tts;

// ---------------------------------------------------------------------------
//  Voice facts that Piper "determines" for the output SpeechQualifier: the
//  PCM sample rate/precision it emits and its language. Loaded from the
//  voice's .onnx.json config so the values are what Piper actually produces.
// ---------------------------------------------------------------------------
public sealed class PiperVoiceProfile
{
    public int SampleRate { get; init; } = 22050;          // Piper "medium" default
    public int SamplePrecisionBits { get; init; } = 16;
    public string? LanguageCode { get; init; }             // e.g. "en-US" (BCP 47)

    public static PiperVoiceProfile Load(string onnxJsonConfigPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(onnxJsonConfigPath));
            var root = doc.RootElement;

            int sampleRate = 22050;
            if (root.TryGetProperty("audio", out var audio) &&
                audio.TryGetProperty("sample_rate", out var sr))
            {
                sampleRate = sr.GetInt32();
            }

            string? language = null;
            if (root.TryGetProperty("language", out var lang) &&
                lang.TryGetProperty("code", out var code))
            {
                // Piper writes "en_US"; normalise to BCP 47 "en-US".
                language = code.GetString()?.Replace('_', '-');
            }

            return new PiperVoiceProfile { SampleRate = sampleRate, LanguageCode = language };
        }
        catch
        {
            return new PiperVoiceProfile();   // fall back to the medium-voice defaults
        }
    }
}

// One Piper voice: the engine that drives it, and the facts it determines.
// Piper is single-voice per model file, so speaking a second language means a
// second voice rather than a second setting.
public sealed record PiperVoice(
    IMpaiTtsV1        Engine,
    PiperVoiceProfile Profile);

// ---------------------------------------------------------------------------
//  MMC-TTS worked example: Basic Text Object -> Basic Speech Object.
//
//  The output Speech Qualifier is built by the three-part transform:
//    inherit   : Language carries over from the input Text Qualifier
//    determine : Format (WAV + PCM), Source = Synthetic, SpeakerType = Agent
//                facts only this (Piper) AIM knows, from what it builds
//    provenance: the spoken text is embedded as ContentDescription.TextObject
//
//  The Language it inherits also CHOOSES THE VOICE when more than one is
//  configured. That is the whole language handoff from Text-to-Text
//  Translation, which stamps the output language on the text it returns.
// ---------------------------------------------------------------------------
public sealed class PiperTtsAim : ITtsAim
{
    private readonly IMpaiTtsV1 _piper;
    private readonly PiperVoiceProfile _voice;

    // Primary language code -> voice. Empty when built with a single voice.
    private readonly IReadOnlyDictionary<string, PiperVoice> _voices;

    private readonly HashSet<string> _warned = new();
    private string _prosodyArgs = "";

    public PiperTtsAim(IMpaiTtsV1 piper, PiperVoiceProfile voice)
        : this(piper, voice, new Dictionary<string, PiperVoice>())
    {
    }

    // Multi-voice form. 'piper' and 'voice' stay the DEFAULT, used when the text
    // carries no language or names one that no configured voice covers.
    public PiperTtsAim(
        IMpaiTtsV1 piper,
        PiperVoiceProfile voice,
        IReadOnlyDictionary<string, PiperVoice> voices)
    {
        _piper  = piper;
        _voice  = voice;
        _voices = voices;
    }

    public Task<BasicSpeechObject> ProcessAsync(BasicTextObject text) => ProcessAsync(text, "");

    // Overload carrying extra Piper flags (prosody) derived from a Speech Personal
    // Status by the AIF processor. Empty => neutral synthesis (unchanged).
    public async Task<BasicSpeechObject> ProcessAsync(BasicTextObject text, string prosodyArgs)
    {
        _prosodyArgs = prosodyArgs ?? "";
        var selected = SelectVoice(text);

        // A voice can fail where the translation succeeded - an installed piper
        // binary too old for a voice's phoneme map, for one. Letting that throw
        // discards the TRANSLATION as well, which is the primary result: the whole
        // Module returns an error and the text nobody can now hear is also text
        // nobody can read.
        //
        // So: try the chosen voice, fall back to the default, and if that fails
        // too, return an EMPTY Speech Object. The Composite AIM then still
        // delivers Output Text, and the log says why nothing was spoken.
        try
        {
            return await SynthesiseAsync(text, selected);
        }
        catch (Exception failure)
        {
            Console.WriteLine(
                $"[MMC-TTS-V2.5] the {selected.Profile.LanguageCode ?? "chosen"} voice failed: " +
                $"{Summarise(failure.Message)}");
        }

        var fallback = new PiperVoice(_piper, _voice);

        if (!ReferenceEquals(selected.Engine, fallback.Engine))
        {
            try
            {
                Console.WriteLine("[MMC-TTS-V2.5] falling back to the default voice.");
                return await SynthesiseAsync(text, fallback);
            }
            catch (Exception failure)
            {
                Console.WriteLine($"[MMC-TTS-V2.5] the default voice failed too: {Summarise(failure.Message)}");
            }
        }

        Console.WriteLine("[MMC-TTS-V2.5] no speech produced; the translated TEXT is still available.");

        return BasicSpeechObject.FromData(
            Array.Empty<byte>(),
            BuildSpeechQualifier(text, _voice));
    }

    private async Task<BasicSpeechObject> SynthesiseAsync(BasicTextObject text, PiperVoice voice)
    {
        // Synthesise: Piper produces WAV bytes from the inline text.
        var wav = await voice.Engine.GenerateAsync(text.GetText(), _prosodyArgs);

        // Build the output Speech Qualifier and attach it to the Basic Speech Object.
        var qualifier = BuildSpeechQualifier(text, voice.Profile);

        return BasicSpeechObject.FromData(wav.SpeechData, qualifier);
    }

    // Piper's errors arrive with a timestamp and a log prefix; the useful part is
    // usually at the end.
    private static string Summarise(string message)
    {
        var trimmed = message.Replace("\r", " ").Replace("\n", " ").Trim();
        return trimmed.Length <= 200 ? trimmed : trimmed.Substring(trimmed.Length - 200);
    }

    private PiperVoice SelectVoice(BasicTextObject text)
    {
        var fallback = new PiperVoice(_piper, _voice);

        if (_voices.Count == 0) return fallback;

        var requested = PrimaryLanguage(text.TextQualifier?.Attributes?.Language?.LanguageCode);
        if (requested.Length == 0) return fallback;

        if (_voices.TryGetValue(requested, out var voice)) return voice;

        // Speaking Italian text in an English voice is a defect, not a detail.
        // Say so once per language rather than let it pass unnoticed.
        if (_warned.Add(requested))
        {
            Console.WriteLine(
                $"[MMC-TTS-V2.5] no voice configured for language '{requested}'; " +
                $"falling back to the default voice ({_voice.LanguageCode ?? "unknown"}). " +
                $"Add a \"Voice:{requested}\" setting to speak it properly.");
        }

        return fallback;
    }

    // "it" / "it-IT" / "it_IT" -> "it". A three-letter ISO 639-3 code is
    // truncated, which is right for the common cases (ita, eng, fra, deu) and
    // wrong for some others; a proper 639-3 to 639-1 table belongs in Mpai.Core
    // if this ever needs to be exact.
    private static string PrimaryLanguage(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode)) return string.Empty;

        var head = languageCode.Trim().ToLowerInvariant().Split('-', '_')[0];

        return head.Length <= 2 ? head : head.Substring(0, 2);
    }

    private SpeechQualifier BuildSpeechQualifier(
        BasicTextObject sourceText,
        PiperVoiceProfile voice)
    {
        // ---- inherit: Language from the input Text Qualifier (fallback: the voice) ----
        Language? language =
            sourceText.TextQualifier?.Attributes?.Language
            ?? (voice.LanguageCode is not null
                ? new Language { LanguageCode = voice.LanguageCode, LanguageFormat = LanguageFormat.Bcp47 }
                : null);

        // ---- determine: what only Piper knows, from what it produced ----
        var format = new SpeechFormat
        {
            ContentFormats = new SpeechContentFormats
            {
                RawData = new Pcm
                {
                    SamplingFrequency = voice.SampleRate,

                    // Precision, not SamplePrecision: the bits used to represent
                    // a sample.
                    Precision = voice.SamplePrecisionBits
                }
            },
            TransportFormats = new SpeechTransportFormats
            {
                FileFormat = SpeechFileFormat.Wav          // Piper emits WAV
            }
        };

        // ---- provenance: embed the spoken text as a one-element Text Object ----
        var contentDescription = new ContentDescription
        {
            TextObject = TextObject.FromBasic(sourceText)
        };

        return new SpeechQualifier
        {
            SpeechQualifierID = Guid.NewGuid().ToString(),
            SubType = new SubType(),
            Format = format,
            Attributes = new SpeechAttributes
            {
                Source = SpeechSource.Synthetic,           // determine: synthetic speech
                Metadata = new SpeechMetadata
                {
                    Language = language,                   // inherited
                    SpeakerProperties = new SpeakerProperties
                    {
                        SpeakerType = SpeakerType.Agent,   // determine: spoken by an agent
                        SpeakerCount = 1
                    },
                    ContentDescription = contentDescription
                }
            }
        };
    }
}