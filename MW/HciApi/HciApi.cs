using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;
using AIF.Store;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Hci.Api;

// The HCI API (MPAI-HCI middleware API). A thin faÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â§ade the User Agent (UAD-MAD)
// uses to drive the MMC-MAD Middleware Module across the north API. MMC-MAD is ONE
// Module (ASR -> EDP -> RSR). The Module is started ONCE and kept alive across turns:
// each turn is one RunAsync on the same instance, so the AIM tree is instantiated
// once (speed) and the running dialogue Summary threads from each turn's
// EditedSummary into the next turn's Summary (context - the dialogue remembers).
// Acquisition (mic) and delivery (loudspeaker, screen) are the UA's real-world
// edges, outside the Module.
public sealed class HciApi : IDisposable
{
    private const string MadModule = "MMC-MAD-V2.5";   // Multimodal Anonymous Dialogue
    private const string MatModule = "MMC-MAT-V2.5";   // Multimodal Anonymous Translation
    private const string RsrModule = "PAF-RSR-V1.6";   // Response and Scene Rendering (say-as-avatar)
    private const string MpdModule = "MMC-MPD-V2.5";   // Multimodal Personal Status-based Dialogue
    private const string AsrModule = "MMC-ASR-V2.5";   // Automatic Speech Recognition (for intent)
    private const string MacModule = "MMC-MAC-V2.5";   // Multimodal Access Control (identify + verdict)

    private readonly UserAgent    _ua;
    private readonly HciProvider  _provider;
    private readonly AimSettings  _settings;

    private int?    _moduleId;         // the started MMC-MAD instance, kept alive across turns
    private int?    _matModuleId;      // the started MMC-MAT instance, kept alive across turns
    private int?    _mpdModuleId;      // the started MMC-MPD instance, kept alive across turns
    private string? _lastSummaryMpd;
    private string? _lastSummary;   // the running dialogue Summary (threaded turn to turn)

    public HciApi(string amdDir, string settingsPath)
    {
        var store = new AmdStore(amdDir);
        store.Scan();
        _settings = AimSettings.Load(settingsPath);
        _provider = new HciProvider(store);
        _ua = new UserAgent(store);
        _ua.MPAI_AIFU_Controller_Initialize();
    }

    // Start MMC-MAD once and keep it alive; subsequent turns reuse the instance.
    private bool EnsureStarted()
    {
        if (_moduleId is not null) return true;
        if (_ua.MPAI_AIFU_MODULE_Start(MadModule, _provider, _settings, out var id) != AifError.OK)
            return false;
        _moduleId = id;
        return true;
    }

    // Drive one dialogue turn through MMC-MAD. Supply the human's turn - typed text
    // and/or a spoken Speech Object - and receive the Speaking Avatar. The running
    // Summary is threaded automatically, so the dialogue keeps context across turns.
    public SpeakingAvatar Converse(string? text = null, BasicSpeechObject? speech = null)
    {
        if (!EnsureStarted()) return new SpeakingAvatar(Array.Empty<byte>(), null);

        var boundary = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(text))
            boundary["TextObject"] = MpaiJson.ToJson(BasicTextObject.FromText(text));
        if (speech is not null && speech.Data.Length > 0)
            boundary["InputSpeech"] = MpaiJson.ToJson(speech);
        if (!string.IsNullOrWhiteSpace(_lastSummary))
            boundary["Summary"] = _lastSummary;

        var (err, outcome) = _ua.RunAsync(_moduleId!.Value, boundary).GetAwaiter().GetResult();
        if (err != AifError.OK || outcome?.Completed is null || outcome.Completed.IsError)
            return new SpeakingAvatar(Array.Empty<byte>(), null);

        var outs = outcome.Completed.Ports;
        byte[] wav = Array.Empty<byte>(); FaceDescriptorsObject? fdo = null;
        if (outs.TryGetValue("MachineSpeech", out var sj) && !string.IsNullOrWhiteSpace(sj))
            wav = MpaiJson.FromJson<BasicSpeechObject>(sj)?.Data ?? Array.Empty<byte>();
        if (outs.TryGetValue("MachineFaceDescriptors", out var fj) && !string.IsNullOrWhiteSpace(fj))
            fdo = MpaiJson.FromJson<FaceDescriptorsObject>(fj);
        // Thread the running Summary into the next turn.
        if (outs.TryGetValue("EditedSummary", out var es) && !string.IsNullOrWhiteSpace(es))
            _lastSummary = es;

        return new SpeakingAvatar(wav, fdo);
    }

    // Personal-Status-based dialogue: one spoken turn through MMC-MPD, which perceives
    // the meaning (Natural Language Understanding) and the feeling (Entity Speech
    // Interpretation + Personal Status Multiplexing) of what the human said, and
    // replies aware of both. The running Summary threads context across turns.
    public SpeakingAvatar ConverseMpd(BasicSpeechObject speech)
    {
        if (_mpdModuleId is null)
        {
            if (_ua.MPAI_AIFU_MODULE_Start(MpdModule, _provider, _settings, out var id) != AifError.OK)
                return new SpeakingAvatar(Array.Empty<byte>(), null);
            _mpdModuleId = id;
        }

        var boundary = new Dictionary<string, string>
        {
            ["InputSpeech"] = MpaiJson.ToJson(speech)
        };
        if (!string.IsNullOrWhiteSpace(_lastSummaryMpd))
            boundary["Summary"] = _lastSummaryMpd;

        var (err, outcome) = _ua.RunAsync(_mpdModuleId!.Value, boundary).GetAwaiter().GetResult();
        if (err != AifError.OK || outcome?.Completed is null || outcome.Completed.IsError)
            return new SpeakingAvatar(Array.Empty<byte>(), null);

        var outs = outcome.Completed.Ports;
        byte[] wav = Array.Empty<byte>(); FaceDescriptorsObject? fdo = null;
        if (outs.TryGetValue("OutputSpeech", out var sj) && !string.IsNullOrWhiteSpace(sj))
            wav = MpaiJson.FromJson<BasicSpeechObject>(sj)?.Data ?? Array.Empty<byte>();
        if (outs.TryGetValue("OutputFaceDescriptors", out var fj) && !string.IsNullOrWhiteSpace(fj))
            fdo = MpaiJson.FromJson<FaceDescriptorsObject>(fj);
        if (outs.TryGetValue("EditedSummary", out var es) && !string.IsNullOrWhiteSpace(es))
            _lastSummaryMpd = es;
        return new SpeakingAvatar(wav, fdo);
    }

    // Recognise speech to text - used by an application to detect a spoken command
    // (an intent) before deciding what to do with the utterance. Runs Automatic
    // Speech Recognition once.
    public string? Recognise(BasicSpeechObject speech)
    {
        var startErr = _ua.MPAI_AIFU_MODULE_Start(AsrModule, _provider, _settings, out var id);
        if (startErr != AifError.OK) return null;
        try
        {
            var boundary = new Dictionary<string, string> { ["InputSpeech"] = MpaiJson.ToJson(speech) };
            var (err, outcome) = _ua.RunAsync(id, boundary).GetAwaiter().GetResult();
            if (err != AifError.OK || outcome?.Completed is null || outcome.Completed.IsError) return null;
            foreach (var kv in outcome.Completed.Ports)
            {
                var payload = kv.Value;
                if (string.IsNullOrWhiteSpace(payload)) continue;
                // Try full Text Object first (ASR outputs OSD-TXO), then Basic Text Object,
                // then a raw JSON "Text" field, so the recognised words are extracted whatever
                // the exact text type.
                try { var t = MpaiJson.FromJson<BasicTextObject>(payload)?.GetText(); if (!string.IsNullOrWhiteSpace(t)) return t; } catch {}
                // Type-agnostic fallback: pull a text field straight from the JSON, whatever the
                // exact text type (OSD-TXO vs OSD-BTO) - the recognised words live in a "Text"-like field.
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(payload);
                    foreach (var field in new[] { "Text", "text", "TextData", "Content", "Recognised", "RecognisedText" })
                        if (doc.RootElement.TryGetProperty(field, out var te) && te.ValueKind == System.Text.Json.JsonValueKind.String)
                        { var t = te.GetString(); if (!string.IsNullOrWhiteSpace(t)) return t; }
                } catch {}
            }
            return null;
        }
        finally { _ua.MPAI_AIFU_MODULE_Stop(id); }
    }


    // Translate one spoken turn through MMC-MAT: the human speaks in one language;
    // the avatar speaks the translation in another, lip-synced. The input language
    // rides on the speech's own Qualifier (ASR reads it there); the target language
    // is named by the Language Selector and carried onward so Text-To-Speech picks
    // the target voice.
    public SpeakingAvatar Translate(BasicSpeechObject speech, string? fromLang, string toLang)
    {
        if (_matModuleId is null)
        {
            if (_ua.MPAI_AIFU_MODULE_Start(MatModule, _provider, _settings, out var mid) != AifError.OK)
                return new SpeakingAvatar(Array.Empty<byte>(), null);
            _matModuleId = mid;
        }

        // Tag the speech with the input language so ASR recognises it in that language.
        var tagged = WithInputLanguage(speech, fromLang);
        var selector = BasicSelectorObject.Languages(fromLang, toLang);

        var boundary = new Dictionary<string, string>
        {
            ["InputSpeech"]      = MpaiJson.ToJson(tagged),
            ["LanguageSelector"] = MpaiJson.ToJson(selector)
        };

        var (err, outcome) = _ua.RunAsync(_matModuleId!.Value, boundary).GetAwaiter().GetResult();
        if (err != AifError.OK || outcome?.Completed is null || outcome.Completed.IsError)
            return new SpeakingAvatar(Array.Empty<byte>(), null);

        var outs = outcome.Completed.Ports;
        byte[] wav = Array.Empty<byte>(); FaceDescriptorsObject? fdo = null;
        if (outs.TryGetValue("MachineSpeech", out var sj) && !string.IsNullOrWhiteSpace(sj))
            wav = MpaiJson.FromJson<BasicSpeechObject>(sj)?.Data ?? Array.Empty<byte>();
        if (outs.TryGetValue("MachineFaceDescriptors", out var fj) && !string.IsNullOrWhiteSpace(fj))
            fdo = MpaiJson.FromJson<FaceDescriptorsObject>(fj);
        string? translated = null;
        if (outs.TryGetValue("TranslatedText", out var tj) && !string.IsNullOrWhiteSpace(tj))
            translated = MpaiJson.FromJson<BasicTextObject>(tj)?.GetText();
        return new SpeakingAvatar(wav, fdo, translated);
    }

    // Set the input language on the speech's Speech Qualifier so ASR recognises it
    // in that language (the Qualifier is where ASR reads the input language). The
    // Qualifier types are init-only, so this rebuilds the qualifier immutably,
    // carrying the existing attributes and setting the language metadata.
    private static BasicSpeechObject WithInputLanguage(BasicSpeechObject speech, string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return speech;
        var oldQ = speech.SpeechQualifier;
        var oldAttr = oldQ?.Attributes;
        var oldMeta = oldAttr?.Metadata;
        var newQualifier = new SpeechQualifier
        {
            SpeechQualifierID = oldQ?.SpeechQualifierID ?? System.Guid.NewGuid().ToString(),
            Attributes = new SpeechAttributes
            {
                Source = oldAttr?.Source ?? SpeechSource.Real,
                Metadata = new SpeechMetadata
                {
                    Language = new Language { LanguageCode = lang, LanguageFormat = "Iso639_1" },
                    SpeakerProperties = oldMeta?.SpeakerProperties
                }
            }
        };
        return BasicSpeechObject.FromData(speech.Data, newQualifier);
    }

    // Say a fixed piece of text as a Speaking Avatar - no dialogue, no translation,
    // just render the given words to speech + a lip-synced face. Used for scripted
    // guidance (for example an access-control app prompting the user). Runs Response
    // and Scene Rendering directly; Personal Status is optional and omitted here, so
    // the avatar speaks in a neutral manner.
    public SpeakingAvatar Announce(string text, string emotion = "CALMNESS", string? attitude = null)
    {
        // Start-run-STOP per call (no keep-alive): each announcement is a fresh RSR
        // run, so no suspend/resume state is carried between prompts. (A kept-alive
        // instance dropped every second prompt - the classic carried-state trap.)
        if (_ua.MPAI_AIFU_MODULE_Start(RsrModule, _provider, _settings, out var rid) != AifError.OK)
            return new SpeakingAvatar(Array.Empty<byte>(), null);

        // The Personal Status here is the MACHINE'S OWN - the expression the avatar
        // should display for this utterance (neutral while capturing, welcoming on
        // success, concerned on failure). It is scripted by the caller, not derived
        // from the user. Response and Scene Rendering turns it into the avatar's
        // facial expression (via Personal Status De-multiplexing + Generative Face
        // Description) alongside the lip-synced speech.
        var boundary = new Dictionary<string, string>
        {
            ["TextObject"]     = MpaiJson.ToJson(BasicTextObject.FromText(text)),
            ["PersonalStatus"] = MpaiJson.ToJson(MachinePersonalStatus(emotion, attitude))
        };

        try
        {
            var (err, outcome) = _ua.RunAsync(rid, boundary).GetAwaiter().GetResult();
            if (err != AifError.OK || outcome?.Completed is null || outcome.Completed.IsError)
                return new SpeakingAvatar(Array.Empty<byte>(), null);

            var outs = outcome.Completed.Ports;
            byte[] wav = Array.Empty<byte>(); FaceDescriptorsObject? fdo = null;
            if (outs.TryGetValue("MachineSpeech", out var sj) && !string.IsNullOrWhiteSpace(sj))
                wav = MpaiJson.FromJson<BasicSpeechObject>(sj)?.Data ?? Array.Empty<byte>();
            if (outs.TryGetValue("MachineFaceDescriptors", out var fj) && !string.IsNullOrWhiteSpace(fj))
                fdo = MpaiJson.FromJson<FaceDescriptorsObject>(fj);
            return new SpeakingAvatar(wav, fdo);
        }
        finally { _ua.MPAI_AIFU_MODULE_Stop(rid); }
    }

    // Build the machine's Personal Status (its OWN expression) from an emotion and an
    // optional attitude - the same shape Entity Dialogue Processing produces, carried
    // in the Text modality for Personal Status De-multiplexing to pick up.

    private static EntityPersonalStatus MachinePersonalStatus(string emotion, string? attitude)
    {
        FactorLabel emo = emotion.ToUpperInvariant() switch
        {
            "HAPPINESS" => FactorLabel.Of("HAPPINESS", "happy", null, 0.8),
            "SADNESS"   => FactorLabel.Of("SADNESS", "sad", null, 0.8),
            "ANGER"     => FactorLabel.Of("ANGER", "disapproving", null, 0.7),
            "FEAR"      => FactorLabel.Of("FEAR", "fearful", null, 0.8),
            "CALMNESS"  => FactorLabel.Of("CALMNESS", "calm", null, 0.6),
            _           => FactorLabel.Of("CALMNESS", "calm", null, 0.5)
        };
        SocialAttitude? att = attitude?.ToLowerInvariant() switch
        {
            "welcoming"    => SocialAttitude.Of(FactorLabel.Of("ACCEPTANCE", "welcoming", null, 0.8)),
            "friendly"     => SocialAttitude.Of(FactorLabel.Of("ACCEPTANCE", "friendly", null, 0.8)),
            "disapproving" => SocialAttitude.Of(FactorLabel.Of("SOCIAL RANK", "disapproving", null, 0.7)),
            _              => null
        };
        return new EntityPersonalStatus
        {
            TextPersonalStatus = new TextPersonalStatus
            {
                TextEmotion        = Emotion.Of(emo),
                TextSocialAttitude = att
            }
        };
    }


    // Begin a fresh conversation: forget the running Summary. The Module stays
    // alive; only the dialogue context is cleared.
    // Run one access-control pass through the CAV-MAC Module: identify the user from
    // their face and speech against the shared gallery, and return the verdict. The UA
    // supplies the acquired Face Object (webcam) and Speech Object (mic); the Module
    // recognises, reconciles, decides, and renders the spoken verdict. A non-empty
    // User ID means access is granted. Start-run-STOP per call (no kept-alive state).
    public AccessResult RunAccessControl(BasicVisualObject? face, BasicSpeechObject? speech)
    {
        if (_ua.MPAI_AIFU_MODULE_Start(MacModule, _provider, _settings, out var id) != AifError.OK)
            return new AccessResult(false, null, new SpeakingAvatar(Array.Empty<byte>(), null));
        try
        {
            var boundary = new Dictionary<string, string>();
            if (face   is not null) boundary["FaceObject"]   = MpaiJson.ToJson(face);
            if (speech is not null) boundary["SpeechObject"] = MpaiJson.ToJson(speech);

            var (err, outcome) = _ua.RunAsync(id, boundary).GetAwaiter().GetResult();
            if (err != AifError.OK || outcome?.Completed is null || outcome.Completed.IsError)
                return new AccessResult(false, null, new SpeakingAvatar(Array.Empty<byte>(), null));

            var outs = outcome.Completed.Ports;
            byte[] wav = Array.Empty<byte>(); FaceDescriptorsObject? fdo = null;
            if (outs.TryGetValue("VocalResponse", out var sj) && !string.IsNullOrWhiteSpace(sj))
                wav = MpaiJson.FromJson<BasicSpeechObject>(sj)?.Data ?? Array.Empty<byte>();
            if (outs.TryGetValue("FaceDescriptors", out var fj) && !string.IsNullOrWhiteSpace(fj))
                fdo = MpaiJson.FromJson<FaceDescriptorsObject>(fj);
            // The UserID port carries the reconciled identity OBJECT (an Instance
            // Identifier). Extract its clean label (the subject name) - never the raw
            // JSON, which must not be shown or spoken.
            string? userId = null;
            if (outs.TryGetValue("UserID", out var uj) && !string.IsNullOrWhiteSpace(uj))
            {
                var iid = MpaiJson.FromJson<InstanceIdentifier>(uj);
                var label = iid?.InstanceIdentifierData is { Count: > 0 } d ? d[0].InstanceLabel : null;
                userId = string.IsNullOrWhiteSpace(label) ? null : label;
            }

            return new AccessResult(userId is not null, userId, new SpeakingAvatar(wav, fdo));
        }
        finally { _ua.MPAI_AIFU_MODULE_Stop(id); }
    }

    public void ResetConversation() => _lastSummary = null;

    public void Dispose()
    {
        if (_moduleId is not null) { _ua.MPAI_AIFU_MODULE_Stop(_moduleId.Value); _moduleId = null; }
        if (_matModuleId is not null) { _ua.MPAI_AIFU_MODULE_Stop(_matModuleId.Value); _matModuleId = null; }
        if (_mpdModuleId is not null) { _ua.MPAI_AIFU_MODULE_Stop(_mpdModuleId.Value); _mpdModuleId = null; }
        _provider.Dispose();
    }
}

// The Speaking Avatar product: Machine Speech (WAV) + the Machine Face Descriptors
// (the facial-animation timeline). The UA presents it on its devices (loudspeaker,
// screen) - the real-world delivery edge.
public sealed record SpeakingAvatar(byte[] MachineSpeechWav, FaceDescriptorsObject? FaceDescriptors, string? TranslatedText = null);

// The access-control verdict: whether access was granted, the reconciled User ID
// (null when not recognised), and the Speaking Avatar the lady uses to voice it.
public sealed record AccessResult(bool Granted, string? UserId, SpeakingAvatar Verdict);
