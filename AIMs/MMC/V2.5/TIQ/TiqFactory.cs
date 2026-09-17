using System;
using System.Collections.Generic;

using Mpai.Core;

namespace Mpai.Mmc.Tiq;

// Builds the BLIP-backed TIQ AIM from deployment settings.
//
// Settings: VisionModel, EncoderModel, DecoderModel, VocabFile
public static class TiqFactory
{
    public static ITiqAim Create(
        IReadOnlyDictionary<string, string> settings)
    {
        return new BlipTiqAim(
            new BlipTiqConfiguration
            {
                VisionModel = Setting(settings, "VisionModel"),
                EncoderModel = Setting(settings, "EncoderModel"),
                DecoderModel = Setting(settings, "DecoderModel"),
                VocabFile = Setting(settings, "VocabFile")
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
                $"MMC-TIQ-V2.5 setting '{key}' is missing.");
        }

        // A RELATIVE SETTING IS RESOLVED AGAINST THE APPLICATION'S OWN ROOT.
        // Every setting here names a file - a model, or the program that reads
        // one - and naming it absolutely binds the installation to one machine.
        // A user must be able to put the folder where they like and run it.
        return Mpai.Core.MpaiPaths.Resolve(value);
    }
}
