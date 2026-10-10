using System;
using System.Collections.Generic;

namespace Mpai.Aims.Asr;

// Builds the Whisper-backed ASR AIM from deployment settings, so tool and
// model locations live in configuration, not in code.
//
// Settings: ExecutablePath, ModelPath, LanguageCode; optional ServerPath, Threads,
// AudioContext, ExtraArguments (switches added to whisper's command lines). ServerPath (whisper-server) keeps the model loaded between turns;
// without it whisper-cli loads it at every turn. AudioContext fixes the encoder
// window; without it the window is sized to each recording.
public static class AsrFactory
{
    public static WhisperAsrAim Create(
        IReadOnlyDictionary<string, string> settings)
    {
        return new WhisperAsrAim(
            new WhisperAsrConfiguration
            {
                ExecutablePath =
                    Setting(settings, "ExecutablePath"),

                ModelPath =
                    Setting(settings, "ModelPath"),

                LanguageCode =
                    settings.TryGetValue("LanguageCode", out var language)
                        ? language
                        : "en",

                ServerPath =
                    settings.TryGetValue("ServerPath", out var server) && !string.IsNullOrWhiteSpace(server)
                        ? Mpai.Core.MpaiPaths.Resolve(server)
                        : null,

                Threads      = Number(settings, "Threads"),
                AudioContext = Number(settings, "AudioContext"),

                ExtraArguments =
                    settings.TryGetValue("ExtraArguments", out var extra) && !string.IsNullOrWhiteSpace(extra)
                        ? extra.Trim()
                        : null
            });
    }

    // An optional whole number; absent, empty or unreadable means "leave the
    // default", so a settings file that predates it runs exactly as before.
    private static int? Number(
        IReadOnlyDictionary<string, string> settings,
        string key) =>
        settings.TryGetValue(key, out var value) && int.TryParse(value, out var n) && n > 0
            ? n
            : null;

    private static string Setting(
        IReadOnlyDictionary<string, string> settings,
        string key)
    {
        if (!settings.TryGetValue(key, out var value) ||
            string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"MMC-ASR-V2.5 setting '{key}' is missing.");
        }

        // A RELATIVE SETTING IS RESOLVED AGAINST THE APPLICATION'S OWN ROOT.
        // Every setting here names a file - a model, or the program that reads
        // one - and naming it absolutely binds the installation to one machine.
        // A user must be able to put the folder where they like and run it.
        return Mpai.Core.MpaiPaths.Resolve(value);
    }
}
