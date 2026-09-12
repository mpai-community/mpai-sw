using System;
using System.Collections.Generic;
using System.IO;

using Mmc.Ttt;
using Mmc.Ttt.Onnx;
using Mpai.Core;   // ITttAim, with the other AIM interfaces

namespace Mpai.Aims.Ttt;

// Builds MMC-TTT-V2.5 from deployment settings.
//
// Settings for the real engine (M2M-100 418M, MIT):
//   TttEncoderModel        encoder_model*.onnx
//   TttDecoderFirstModel   decoder_model*.onnx            (first step, no past)
//   TttDecoderPastModel    decoder_with_past_model*.onnx  (every later step)
//   TttSpmModel        sentencepiece.bpe.model
//   TttVocab           vocab.json
//   TttSpecialTokens   special_tokens_map.json  (optional but wanted: it carries
//                      the ORDERED language list the ids are derived from)
//
// TttDecoderModel (the MERGED decoder) is deliberately unused: it failed on the
// first decode step inside its own optimum::if cache-branch switch. The setting
// can stay in aim-settings.json; nothing reads it.
//
// With any of the five required settings missing, the marker engine is used so
// the pipeline still runs. That is a scaffold, not a fallback to be relied on:
// it says so on every call.
public static class TttFactory
{
    public static ITttAim Create(
        IReadOnlyDictionary<string, string> settings)
    {
        var encoder = Value(settings, "TttEncoderModel");
        var first   = Value(settings, "TttDecoderFirstModel");
        var past    = Value(settings, "TttDecoderPastModel");
        var spm     = Value(settings, "TttSpmModel");
        var vocab   = Value(settings, "TttVocab");
        var special = Value(settings, "TttSpecialTokens");

        var missing = new List<string>();
        if (encoder is null) missing.Add("TttEncoderModel");
        if (first   is null) missing.Add("TttDecoderFirstModel");
        if (past    is null) missing.Add("TttDecoderPastModel");
        if (spm     is null) missing.Add("TttSpmModel");
        if (vocab   is null) missing.Add("TttVocab");

        if (missing.Count > 0)
        {
            Console.WriteLine(
                $"[MMC-TTT-V2.5] no translation model ({string.Join(", ", missing)} not set) " +
                "- using MarkerTttAim; output is NOT translated.");

            return new MarkerTttAim();
        }

        foreach (var path in new[] { encoder!, first!, past!, spm!, vocab! })
        {
            if (!File.Exists(path))
            {
                Console.WriteLine(
                    $"[MMC-TTT-V2.5] {path} is missing - using MarkerTttAim; " +
                    "output is NOT translated.");

                return new MarkerTttAim();
            }
        }

        if (special is null)
        {
            Console.WriteLine(
                "[MMC-TTT-V2.5] TttSpecialTokens not set; the language order falls back to " +
                "the embedded list. Verify with --tokentest.");
        }

        return new OnnxTttAim(
            new OnnxTttConfiguration
            {
                EncoderModelPath      = encoder!,
                DecoderFirstModelPath = first!,
                DecoderPastModelPath  = past!,
                SpmModelPath          = spm!,
                VocabPath             = vocab!,
                SpecialTokensPath     = special
            });
    }

    private static string? Value(
        IReadOnlyDictionary<string, string> settings,
        string key) =>
        settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
}