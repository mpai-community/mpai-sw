using System;
using System.Collections.Generic;

namespace Mpai.Aims.Asr;

// Builds the Whisper-backed ASR AIM from deployment settings, so tool and
// model locations live in configuration, not in code.
//
// Settings: ExecutablePath, ModelPath, LanguageCode
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
                        : "en"
            });
    }

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
