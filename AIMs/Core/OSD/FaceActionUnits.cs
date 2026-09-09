using System;
using System.Collections.Generic;
using System.Linq;

namespace Mpai.Core.OSD;

// FACS Action Units - a facial pose coded as Facial Action Coding System (Ekman)
// Action Unit activations, each a weight in [0,1]. Renderer-agnostic: the same AU
// descriptor drives a 2D face, a 3D FACS avatar, or any blendshape rig. A single
// FaceActionUnits is ONE FRAME of the facial animation; a sequence of frames over
// time (a FaceDescriptorsObject) is the animation the Generative Face Description
// produces.
//
// The AU set covers the six basic emotions (via EM-FACS) plus the mouth AUs used
// for lip-sync visemes.
public enum ActionUnit
{
    AU1_InnerBrowRaise = 1,
    AU2_OuterBrowRaise = 2,
    AU4_BrowLower      = 4,
    AU5_UpperLidRaise  = 5,
    AU6_CheekRaise     = 6,
    AU7_LidTighten     = 7,
    AU9_NoseWrinkle    = 9,
    AU12_LipCornerPull = 12,   // smile
    AU15_LipCornerDepress = 15,// frown
    AU17_ChinRaise     = 17,
    AU18_LipPucker     = 18,   // rounded visemes (o/u)
    AU20_LipStretch    = 20,   // wide visemes (e/i)
    AU23_LipTighten    = 23,
    AU25_LipsPart      = 25,
    AU26_JawDrop       = 26    // open visemes (a/o), mouth open
}

// A set of Action Unit activations (AU -> weight in [0,1]) - one animation frame.
public sealed class FaceActionUnits
{
    public string ContentFormat { get; init; } = "FACS-AU";
    public Dictionary<string, double> ActionUnits { get; init; } = new();

    public double Weight(ActionUnit au) =>
        ActionUnits.TryGetValue(au.ToString(), out var w) ? w : 0.0;

    public static FaceActionUnits Of(IReadOnlyDictionary<ActionUnit, double> weights) => new()
    {
        ActionUnits = weights.ToDictionary(
            kv => kv.Key.ToString(),
            kv => Math.Clamp(kv.Value, 0.0, 1.0))
    };

    // Combine two AU sets (expression + viseme), taking the max per AU so a smile and
    // an open-mouth viseme coexist rather than overwrite.
    public FaceActionUnits MergedWith(FaceActionUnits other)
    {
        var merged = new Dictionary<string, double>(ActionUnits);
        foreach (var (k, v) in other.ActionUnits)
            merged[k] = Math.Max(merged.TryGetValue(k, out var e) ? e : 0.0, v);
        return new FaceActionUnits { ContentFormat = ContentFormat, ActionUnits = merged };
    }
}

// EM-FACS: maps a basic Emotion (Ekman category) to the Action Units that express
// it, scaled by intensity - the standard prototypes (happiness = AU6 + AU12, etc.).
public static class EmFacs
{
    private static readonly Dictionary<string, ActionUnit[]> Prototypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HAPPINESS"] = new[] { ActionUnit.AU6_CheekRaise, ActionUnit.AU12_LipCornerPull },
        ["SADNESS"]   = new[] { ActionUnit.AU1_InnerBrowRaise, ActionUnit.AU4_BrowLower, ActionUnit.AU15_LipCornerDepress },
        ["ANGER"]     = new[] { ActionUnit.AU4_BrowLower, ActionUnit.AU5_UpperLidRaise, ActionUnit.AU7_LidTighten, ActionUnit.AU23_LipTighten },
        ["FEAR"]      = new[] { ActionUnit.AU1_InnerBrowRaise, ActionUnit.AU2_OuterBrowRaise, ActionUnit.AU4_BrowLower, ActionUnit.AU5_UpperLidRaise, ActionUnit.AU20_LipStretch, ActionUnit.AU26_JawDrop },
        ["DISGUST"]   = new[] { ActionUnit.AU9_NoseWrinkle, ActionUnit.AU15_LipCornerDepress, ActionUnit.AU17_ChinRaise },
        ["SURPRISE"]  = new[] { ActionUnit.AU1_InnerBrowRaise, ActionUnit.AU2_OuterBrowRaise, ActionUnit.AU5_UpperLidRaise, ActionUnit.AU26_JawDrop },
        ["CALMNESS"]  = Array.Empty<ActionUnit>()
    };

    public static FaceActionUnits ToActionUnits(string? emotionCategory, double intensity)
    {
        double w = Math.Clamp(intensity, 0.0, 1.0);
        var weights = new Dictionary<ActionUnit, double>();
        if (emotionCategory is not null && Prototypes.TryGetValue(emotionCategory, out var aus))
            foreach (var au in aus) weights[au] = w;
        return FaceActionUnits.Of(weights);
    }
}

// Visemes - a small set of mouth shapes, each mapped to FACS mouth AUs. A phoneme
// maps to a viseme; the viseme sets the mouth AUs for a frame. This is the lip part
// of the facial animation (the FDO), driven by the text's phonemes and the speech's
// timing.
public enum Viseme { Silence, Open, Wide, Round, Closed, Neutral }

public static class Visemes
{
    // Viseme -> mouth AU activations.
    public static FaceActionUnits ToActionUnits(Viseme v) => v switch
    {
        Viseme.Open    => Aus((ActionUnit.AU26_JawDrop, 0.7), (ActionUnit.AU25_LipsPart, 0.6)),   // a, o open
        Viseme.Wide    => Aus((ActionUnit.AU20_LipStretch, 0.7), (ActionUnit.AU25_LipsPart, 0.3)),// e, i
        Viseme.Round   => Aus((ActionUnit.AU18_LipPucker, 0.8), (ActionUnit.AU25_LipsPart, 0.2)), // o, u
        Viseme.Closed  => Aus((ActionUnit.AU23_LipTighten, 0.4)),                                 // p, b, m
        Viseme.Neutral => Aus((ActionUnit.AU25_LipsPart, 0.2)),                                   // relaxed slightly open
        _              => Aus()                                                                    // Silence -> mouth closed
    };

    // Map an espeak-ng IPA/phoneme token to a viseme (coarse but correct-enough
    // grouping). Works on the phoneme STRING - IPA symbols are multi-byte, so a
    // single C# char cannot hold them; we test the leading symbol as a string.
    private static readonly string OpenV   = "aÉ‘ÊŒÉ’Ã¦";
    private static readonly string WideV    = "eiÉªÉ›jfvszÊƒÎ¸";
    private static readonly string RoundV   = "ouÊŠÉ”w";
    private static readonly string ClosedV  = "pbm";

    public static Viseme FromPhoneme(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return Viseme.Silence;
        // Drop IPA stress marks, take the first symbol as a string element.
        string t = p.Trim().TrimStart('\u02C8', '\u02CC'); // Ëˆ ËŒ
        if (t.Length == 0) return Viseme.Silence;
        var si = System.Globalization.StringInfo.GetNextTextElement(t.ToLowerInvariant());
        if (string.IsNullOrEmpty(si)) return Viseme.Neutral;
        if (OpenV.Contains(si))   return Viseme.Open;
        if (RoundV.Contains(si))  return Viseme.Round;
        if (WideV.Contains(si))   return Viseme.Wide;
        if (ClosedV.Contains(si)) return Viseme.Closed;
        return Viseme.Neutral;
    }

    private static FaceActionUnits Aus(params (ActionUnit au, double w)[] items)
        => FaceActionUnits.Of(items.ToDictionary(i => i.au, i => i.w));
}
