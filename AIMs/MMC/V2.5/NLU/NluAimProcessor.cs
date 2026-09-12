using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using AIF.Controller;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Mmc.Nlu;

// MMC-NLU-V2.5 - Natural Language Understanding, as an AIF IAimProcessor.
//
// Receives an input text (Input Text typed directly, or Recognised Text from ASR)
// and produces:
//   - the Text Descriptors Object (MMC-TDO) carrying the Basic Text Descriptors
//     (the four taggings POS/NE/dependency/SRL),
//   - a Refined Text (the cleaned-up input), and
//   - the Text Personal Status (MMC-TPS): a value in [0,1] for each Personal Status
//     Factor (Cognitive State, Emotion, Social Attitude) carried by the text.
// The Text Descriptors serve the understanding/dialogue path; the Text Personal
// Status serves Personal Status Extraction, which consumes only the Personal Status.
//
// ENGINE (first pass, deliberately simple - proves the AIM and the data shapes
// end-to-end through the Controller, then deepens without touching the interface,
// the same way the detectors and the tagger were proven before being perfected):
//   - Refined Text: trims/normalises whitespace.
//   - POS_tagging:  a small lexicon + suffix-rule tagger over the tokens.
//   - NE_tagging:   capitalised-token / simple-pattern named-entity spotting.
//   - dependency_tagging, SRL_tagging: left null (the spec permits null taggings).
//   - Text Personal Status: a small affect lexicon estimates the three factors.
//     (The planned upgrade is a contextual text-affect model, e.g. GoEmotions, run
//     locally; the interface does not change when the engine is deepened.)
public sealed class NluAimProcessor : IAimProcessor
{
    private const string PosSet = "MPAI-NLU-simple-POS";
    private const string NeSet  = "MPAI-NLU-simple-NE";

    private readonly string _instanceId;
    private readonly string _inputTextPort;       // OSD-BTO PortNumber 1
    private readonly string _recognisedTextPort;  // OSD-BTO PortNumber 2
    private readonly string _textDescriptorsPort; // MMC-TDO
    private readonly string _refinedTextPort;     // OSD-BTO
    private readonly string _textPsPort;          // MMC-TPS

    public NluAimProcessor(string instanceId, AimPortReader ports)
    {
        _instanceId          = instanceId;
        _inputTextPort       = ports.Input("OSD-BTO-V1.5", 1);
        _recognisedTextPort  = ports.Input("OSD-BTO-V1.5", 2);
        _textDescriptorsPort = ports.Output("MMC-TDO-V2.5");
        _refinedTextPort     = ports.Output("OSD-BTO-V1.5");
        _textPsPort          = ports.Output("MMC-TPS-V2.5");
    }

    public string InstanceId => _instanceId;

    public System.Threading.Tasks.Task<Message> ProcessAsync(Message message)
    {
        // Prefer Recognised Text (from ASR) if present, else the directly-typed Input Text.
        string? text = ReadText(message, _recognisedTextPort) ?? ReadText(message, _inputTextPort);
        if (string.IsNullOrWhiteSpace(text))
            return System.Threading.Tasks.Task.FromResult(
                Message.Error(message.MessageId, _instanceId, "no Input Text or Recognised Text on input ports"));

        string refined = Refine(text);
        var basic = Analyse(refined);
        var textDescriptors = TextDescriptorsObject.FromBasic(basic);
        var refinedObject = BasicTextObject.FromText(refined);
        var textPersonalStatus = EstimatePersonalStatus(refined);

        return System.Threading.Tasks.Task.FromResult(new Message
        {
            MessageId = message.MessageId,
            MessageType = message.MessageType,
            Ports = new Dictionary<string, string>
            {
                [_textDescriptorsPort] = MpaiJson.ToJson(textDescriptors),
                [_refinedTextPort]     = MpaiJson.ToJson(refinedObject),
                [_textPsPort]          = MpaiJson.ToJson(textPersonalStatus)
            }
        });
    }

    private static string? ReadText(Message message, string port)
    {
        if (!message.Ports.TryGetValue(port, out var json) || string.IsNullOrWhiteSpace(json))
            return null;
        var txo = MpaiJson.FromJson<BasicTextObject>(json);
        return txo?.GetText();
    }

    // Refined Text: normalise whitespace; keep the content otherwise intact.
    private static string Refine(string text)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Trim();
    }

    // Produce the Basic Text Descriptors (the four taggings) for the text. POS and
    // NE are filled by the simple tagger; dependency and SRL are left null.
    private static BasicTextDescriptors Analyse(string text)
    {
        var tokens = Tokenise(text);
        return new BasicTextDescriptors
        {
            BasicTextDescriptorsID = Guid.NewGuid().ToString(),
            TextDescriptorsData = new TextDescriptorsData
            {
                POS_tagging = new Tagging { Set = PosSet, Result = PosTag(tokens) },
                NE_tagging  = new Tagging { Set = NeSet,  Result = NeTag(tokens) },
                dependency_tagging = null,
                SRL_tagging        = null
            }
        };
    }

    // First-pass Text Personal Status: estimate the three Factors from the text
    // with a small affect lexicon, choosing a LABEL (Category + adjectival) from each
    // Factor's standard set and a Degree in [0,1]. Emotion from positive/negative
    // valence words; Cognitive State from certainty/uncertainty markers; Social
    // Attitude from polite/aggressive markers. A Factor with no signal is left null;
    // at least one is always emitted (a calm Emotion as the neutral default).
    // (The planned upgrade is a contextual affect model, e.g. GoEmotions, run locally;
    // the interface does not change when the engine is deepened.)
    private static TextPersonalStatus EstimatePersonalStatus(string text)
    {
        var tokens = Tokenise(text).Select(t => t.ToLowerInvariant()).ToList();

        int pos = tokens.Count(PositiveWords.Contains);
        int neg = tokens.Count(NegativeWords.Contains);
        int certain   = tokens.Count(CertaintyWords.Contains);
        int uncertain = tokens.Count(UncertaintyWords.Contains);
        int polite     = tokens.Count(PoliteWords.Contains);
        int aggressive = tokens.Count(AggressiveWords.Contains);

        // Emotion
        Emotion? emotion = null;
        if (pos > neg)      emotion = Emotion.Of(FactorLabel.Of("HAPPINESS", "happy", null, Degree(pos)));
        else if (neg > pos) emotion = Emotion.Of(FactorLabel.Of("SADNESS", "sad", null, Degree(neg)));

        // Cognitive State
        CognitiveState? cognitiveState = null;
        if (certain > uncertain)      cognitiveState = CognitiveState.Of(FactorLabel.Of("BELIEF", "credulous", null, Degree(certain)));
        else if (uncertain > certain) cognitiveState = CognitiveState.Of(FactorLabel.Of("UNDERSTANDING", "comprehending", "bewildered/puzzled", Degree(uncertain)));

        // Social Attitude
        SocialAttitude? socialAttitude = null;
        if (polite > aggressive)      socialAttitude = SocialAttitude.Of(FactorLabel.Of("SOCIAL RANK", "respectful", null, Degree(polite)));
        else if (aggressive > polite) socialAttitude = SocialAttitude.Of(FactorLabel.Of("AGGRESSION", "aggressive", null, Degree(aggressive)));

        // Ensure at least one Factor is present: default to a calm Emotion.
        if (emotion is null && cognitiveState is null && socialAttitude is null)
            emotion = Emotion.Of(FactorLabel.Of("CALMNESS", "calm", null, 0.5));

        return new TextPersonalStatus
        {
            TextCognitiveState = cognitiveState,
            TextEmotion        = emotion,
            TextSocialAttitude = socialAttitude
        };
    }

    // Map a signal count to a Degree in [0,1] (more mentions -> higher confidence).
    private static double Degree(int count) => Math.Min(1.0, 0.4 + 0.3 * count);

    private static List<string> Tokenise(string text)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c)) { sb.Append(c); }
            else
            {
                if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
                if (char.IsPunctuation(c)) tokens.Add(c.ToString());
            }
        }
        if (sb.Length > 0) tokens.Add(sb.ToString());
        return tokens;
    }

    // A tiny closed-class lexicon + suffix rules. Output: space-separated token/TAG.
    private static string PosTag(List<string> tokens)
    {
        var tags = tokens.Select(t => $"{t}/{PosOf(t)}");
        return string.Join(' ', tags);
    }

    private static string PosOf(string token)
    {
        if (token.Length == 1 && char.IsPunctuation(token[0])) return "PUNCT";
        if (token.All(char.IsDigit)) return "NUM";

        string w = token.ToLowerInvariant();
        if (Determiners.Contains(w)) return "DET";
        if (Pronouns.Contains(w))    return "PRON";
        if (Prepositions.Contains(w))return "ADP";
        if (Conjunctions.Contains(w))return "CCONJ";
        if (Auxiliaries.Contains(w)) return "AUX";

        if (w.EndsWith("ly")) return "ADV";
        if (w.EndsWith("ing") || w.EndsWith("ed")) return "VERB";
        if (w.EndsWith("tion") || w.EndsWith("ness") || w.EndsWith("ment")) return "NOUN";

        if (token.Length > 0 && char.IsUpper(token[0])) return "PROPN";
        return "NOUN";
    }

    private static string NeTag(List<string> tokens)
    {
        var spans = new List<string>();
        var current = new List<string>();
        foreach (var t in tokens)
        {
            bool capitalised = t.Length > 0 && char.IsUpper(t[0]) && t.Any(char.IsLetter);
            if (capitalised) { current.Add(t); }
            else if (current.Count > 0) { spans.Add(string.Join(' ', current)); current.Clear(); }
        }
        if (current.Count > 0) spans.Add(string.Join(' ', current));
        return string.Join("; ", spans.Select(s => $"{s}:ENT"));
    }

    private static readonly HashSet<string> Determiners  = new() { "the", "a", "an", "this", "that", "these", "those", "my", "your", "his", "her", "its", "our", "their" };
    private static readonly HashSet<string> Pronouns     = new() { "i", "you", "he", "she", "it", "we", "they", "me", "him", "them", "us", "who", "what", "which" };
    private static readonly HashSet<string> Prepositions = new() { "in", "on", "at", "to", "from", "with", "by", "for", "of", "about", "into", "over", "under", "near" };
    private static readonly HashSet<string> Conjunctions = new() { "and", "or", "but", "nor", "so", "yet" };
    private static readonly HashSet<string> Auxiliaries  = new() { "is", "am", "are", "was", "were", "be", "been", "being", "do", "does", "did", "have", "has", "had", "will", "would", "can", "could", "shall", "should", "may", "might", "must" };

    // Small affect lexicon for the first-pass Text Personal Status estimate.
    private static readonly HashSet<string> PositiveWords    = new() { "good", "great", "happy", "glad", "love", "wonderful", "excellent", "pleased", "delighted", "thanks", "thank", "nice", "fine", "welcome", "yes" };
    private static readonly HashSet<string> NegativeWords    = new() { "bad", "terrible", "hate", "angry", "sad", "upset", "awful", "annoyed", "wrong", "no", "never", "problem", "sorry", "afraid", "worried" };
    private static readonly HashSet<string> CertaintyWords   = new() { "sure", "certain", "definitely", "clearly", "obviously", "know", "convinced", "confident", "absolutely", "indeed" };
    private static readonly HashSet<string> UncertaintyWords = new() { "maybe", "perhaps", "possibly", "might", "guess", "unsure", "confused", "dubious", "doubt", "wonder" };
    private static readonly HashSet<string> PoliteWords      = new() { "please", "thanks", "thank", "kindly", "would", "could", "sorry", "welcome", "appreciate" };
    private static readonly HashSet<string> AggressiveWords  = new() { "now", "immediately", "must", "demand", "stupid", "shut", "hate", "idiot" };
}
