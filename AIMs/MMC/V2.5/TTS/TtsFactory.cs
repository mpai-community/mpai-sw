using System;
using System.Collections.Generic;
using System.IO;

using Mmc.Tts;
using Mmc.Tts.Piper;

namespace Mpai.Aims.Tts;

// Builds the Piper-backed TTS AIM from deployment settings.
//
// Settings:
//   PiperExecutable            piper(.exe)
//   VoiceModel, VoiceConfig    the DEFAULT voice
//   Voice:<lang>               a voice for that language, e.g.
//                              "Voice:it" : "D:\...\it_IT-riccardo-x_low.onnx"
//   VoiceConfig:<lang>         optional; defaults to "<Voice:lang>.json",
//                              which is Piper's own naming convention
//
// Piper is one voice per model file, so a multilingual pipeline needs one entry
// per language. With no Voice:<lang> settings the AIM behaves exactly as before:
// a single voice, whatever the language of the text.
public static class TtsFactory
{
    private const string VoicePrefix  = "Voice:";
    private const string ConfigPrefix = "VoiceConfig:";

    public static PiperTtsAim Create(
        IReadOnlyDictionary<string, string> settings)
    {
        var runner =
            new PiperProcessRunner(
                new PiperConfiguration
                {
                    ExecutablePath = Setting(settings, "PiperExecutable")
                });

        var defaultVoice =
            BuildVoice(
                runner,
                Setting(settings, "VoiceModel"),
                Setting(settings, "VoiceConfig"));

        var voices =
            BuildVoices(runner, settings, defaultVoice);

        return new PiperTtsAim(
            defaultVoice.Engine,
            defaultVoice.Profile,
            voices);
    }

    // One voice per "Voice:<lang>" setting, plus the default voice registered
    // under its own language so that naming it explicitly also works.
    private static Dictionary<string, PiperVoice> BuildVoices(
        IPiperProcessRunner runner,
        IReadOnlyDictionary<string, string> settings,
        PiperVoice defaultVoice)
    {
        var voices = new Dictionary<string, PiperVoice>();

        var defaultLanguage = Primary(defaultVoice.Profile.LanguageCode);
        if (defaultLanguage.Length > 0)
        {
            voices[defaultLanguage] = defaultVoice;
        }

        foreach (var setting in settings)
        {
            if (!setting.Key.StartsWith(VoicePrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var language = Primary(setting.Key.Substring(VoicePrefix.Length));
            if (language.Length == 0 || string.IsNullOrWhiteSpace(setting.Value))
                continue;

            var modelPath = setting.Value;

            // Piper ships "x.onnx" beside "x.onnx.json"; allow an override.
            var configKey  = ConfigPrefix + setting.Key.Substring(VoicePrefix.Length);
            var configPath =
                settings.TryGetValue(configKey, out var explicitConfig) &&
                !string.IsNullOrWhiteSpace(explicitConfig)
                    ? explicitConfig
                    : modelPath + ".json";

            if (!File.Exists(modelPath))
            {
                Console.WriteLine(
                    $"[MMC-TTS-V2.5] voice for '{language}' not found at {modelPath}; ignored.");
                continue;
            }

            voices[language] = BuildVoice(runner, modelPath, configPath);
        }

        if (voices.Count > 1)
        {
            Console.WriteLine(
                $"[MMC-TTS-V2.5] voices configured: {string.Join(", ", voices.Keys)}");
        }

        return voices;
    }

    private static PiperVoice BuildVoice(
        IPiperProcessRunner runner,
        string modelPath,
        string configPath)
    {
        var configuration =
            new MpaiTtsV1Configuration
            {
                ModelPath  = modelPath,
                ConfigPath = configPath
            };

        var engine =
            new MpaiTtsV1(
                runner,
                new SpeechObjectBuilder(),
                configuration);

        return new PiperVoice(
            engine,
            PiperVoiceProfile.Load(configPath));
    }

    // "it" / "it-IT" / "it_IT" -> "it".
    private static string Primary(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode)) return string.Empty;

        var head = languageCode.Trim().ToLowerInvariant().Split('-', '_')[0];

        return head.Length <= 2 ? head : head.Substring(0, 2);
    }

    private static string Setting(
        IReadOnlyDictionary<string, string> settings,
        string key)
    {
        if (!settings.TryGetValue(key, out var value) ||
            string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"MMC-TTS-V2.5 setting '{key}' is missing.");
        }

        return value;
    }
}