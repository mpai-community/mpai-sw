using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

using AIF.Controller;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Mmc.Edp;

// MMC-EDP-V2.5 - Entity Dialogue Processing, as an AIF IAimProcessor.
//
// Composes the human's situational picture - what they said (Basic Text Object),
// its meaning (Text Descriptors), how they are (Personal Status), who they are
// (User ID), the scene's objects (Object IIDs), and the Basic Audio-Visual Scene
// (BMS) - together with the running Summary (dialogue memory), into a prompt for a
// local LLM (Ollama). It produces the Machine's response Text, the Machine's own
// Personal Status (how the machine chooses to present itself), and an updated
// Summary. The LLM is asked to return a small JSON block so the machine's Personal
// Status is structured enough to drive avatar rendering downstream.
public sealed class EdpAimProcessor : IAimProcessor
{
    private readonly string _instanceId;
    private readonly OllamaClient _llm;
    private string _sessionSummary = String.Empty;   // running dialogue memory, kept INSIDE the AIM (session lifetime)

    private readonly string _summaryPort;    // MMC-SUM
    private readonly string _textPort;       // OSD-BTO
    private readonly string _descriptorsPort;// MMC-TDO
    private readonly string _psPort;         // MMC-EPS
    private readonly string _userIdPort;     // OSD-IID #1
    private readonly string _visualObjectIdsPort; // OSD-IID #2
    private readonly string _audioObjectIdsPort;  // OSD-IID #3
    private readonly string _bmsPort;        // OSD-BMS

    private readonly string _outTextPort;    // OSD-BTO
    private readonly string _outPsPort;      // MMC-EPS
    private readonly string _outSummaryPort; // MMC-SUM

    public EdpAimProcessor(string instanceId, OllamaClient llm, AimPortReader ports)
    {
        _instanceId       = instanceId;
        _llm              = llm;
        _summaryPort      = ports.Input("MMC-SUM-V2.5");
        _textPort         = ports.Input("OSD-BTO-V1.5");
        _descriptorsPort  = ports.Input("MMC-TDO-V2.5");
        _psPort           = ports.Input("MMC-EPS-V2.5");
        _userIdPort       = ports.Input("OSD-IID-V1.5", 1);
        _visualObjectIdsPort = ports.Input("OSD-IID-V1.5", 2);
        _audioObjectIdsPort  = ports.Input("OSD-IID-V1.5", 3);
        _bmsPort          = ports.Input("OSD-BMS-V1.5");
        _outTextPort      = ports.Output("OSD-BTO-V1.5");
        _outPsPort        = ports.Output("MMC-EPS-V2.5");
        _outSummaryPort   = ports.Output("MMC-SUM-V2.5");
    }

    public string InstanceId => _instanceId;

    public System.Threading.Tasks.Task<Message> ProcessAsync(Message message)
    {
        string? userText = ReadText(message, _textPort);
        if (string.IsNullOrWhiteSpace(userText))
            return System.Threading.Tasks.Task.FromResult(
                Message.Error(message.MessageId, _instanceId, "no Text Object on input port"));

        var psIn        = Read<EntityPersonalStatus>(message, _psPort);   // MMC-EPS in (may be absent)
        bool affect     = psIn is not null;                               // EPS in -> affect path; else plain
        string userStatus = affect ? VerbalisePersonalStatus(psIn) : "";
        string userId     = ReadInstanceLabel(message, _userIdPort);
        string sceneClause = VerbaliseScene(message);
        string summaryIn   = _sessionSummary;   // memory is internal; UA does not supply Summary

        // System prompt. EDP INPUT->OUTPUT RULE: the machine produces a Personal
        // Status ONLY when a Personal Status was provided as input. With no EPS in
        // (e.g. anonymous dialogue), we neither ask the LLM for affect nor emit a
        // machine EPS - the avatar renders neutrally.
        string system = affect
            ? "You are the CAV, a courteous conversational machine holding a face-to-face " +
              "conversation with a person. Reply naturally and briefly to what the person said. " +
              "Adapt your TONE to be appropriate and empathetic to how the person seems to feel, " +
              "but NEVER mention, describe, name, or refer to their emotion, mood, feelings, or " +
              "personal status - just respond naturally to what they say. " +
              "Then choose how YOU present yourself. " +
              "Return ONLY a compact JSON object with these exact keys: " +
              "\"response\" (your spoken reply as a short plain string - no labels, no JSON inside it), " +
              "\"emotion\" (one of HAPPINESS, CALMNESS, SADNESS, ANGER, FEAR, NEUTRAL), " +
              "\"attitude\" (one of respectful, friendly, confident, neutral), " +
              "\"summary\" (a one-sentence updated running summary). " +
              "Output ONLY the JSON object and nothing else - no code fences, no prose before or after."
            : "You are the CAV, a courteous conversational machine holding a face-to-face " +
              "conversation with a person. Reply naturally and briefly to what the person said. " +
              "Return ONLY your spoken reply as plain text - no JSON, no labels, no commentary.";

        var prompt = new StringBuilder();
        prompt.Append("Please respond to the following Text provided by the user");
        if (!string.IsNullOrWhiteSpace(userId)) prompt.Append($" with ID {userId}");
        if (!string.IsNullOrWhiteSpace(sceneClause))
            prompt.Append($", who is located in a scene populated by {sceneClause}");
        prompt.AppendLine(".");
        // The user's affect conditions TONE only - given to the model as private
        // guidance, never to be echoed or named in the reply.
        if (!string.IsNullOrWhiteSpace(userStatus))
            prompt.AppendLine($"(Private tone guidance - do NOT mention this to the user: they currently seem {userStatus}. Respond with matching empathy, without ever naming their state.)");
        if (!string.IsNullOrWhiteSpace(summaryIn))
            prompt.AppendLine($"The conversation so far: {summaryIn}");
        prompt.AppendLine($"Text: \"{userText}\"");

        string reply;
        try
        {
            reply = _llm.ChatAsync(system, prompt.ToString()).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return System.Threading.Tasks.Task.FromResult(
                Message.Error(message.MessageId, _instanceId, $"LLM call failed (is Ollama running?): {ex.Message}"));
        }

        // Determine the spoken response (and, in the affect path, the machine EPS).
        string responseText;
        EntityPersonalStatus? machinePs = null;
        if (affect)
        {
            var (rt, emotion, attitude, _) = ParseReply(reply, userText);
            responseText = rt;
            machinePs = MachinePersonalStatus(emotion, attitude);
        }
        else
        {
            responseText = reply.Trim();   // plain text; no JSON, no affect
        }

        var machineText = BasicTextObject.FromText(responseText);
        var transcript = string.IsNullOrWhiteSpace(summaryIn)
            ? $"User: {userText}\nCAV: {responseText}"
            : $"{summaryIn}\nUser: {userText}\nCAV: {responseText}";
        var editedSummary = Summary.Of(transcript);
        _sessionSummary = transcript;   // keep memory inside the AIM for the next turn

        var ports = new Dictionary<string, string>
        {
            [_outTextPort]    = MpaiJson.ToJson(machineText),
            [_outSummaryPort] = MpaiJson.ToJson(editedSummary)
        };
        if (machinePs is not null)                       // EPS out ONLY if EPS was in
            ports[_outPsPort] = MpaiJson.ToJson(machinePs);

        return System.Threading.Tasks.Task.FromResult(new Message
        {
            MessageId = message.MessageId,
            MessageType = message.MessageType,
            Ports = ports
        });
    }

    // Verbalise the user's Personal Status into a natural phrase for the prompt,
    // e.g. "happy (face), calm (voice), respectful (words)".
    private static string VerbalisePersonalStatus(EntityPersonalStatus? eps)
    {
        if (eps is null) return "";
        var parts = new List<string>();
        void Add(string? cat, string? gen, string modality)
        {
            var label = gen ?? cat;
            if (!string.IsNullOrWhiteSpace(label)) parts.Add($"{label} ({modality})");
        }
        Add(eps.TextPersonalStatus?.TextSocialAttitude?.Category, eps.TextPersonalStatus?.TextSocialAttitude?.GeneralAdjectival, "words");
        Add(eps.SpeechPersonalStatus?.SpeechEmotion?.Category, eps.SpeechPersonalStatus?.SpeechEmotion?.GeneralAdjectival, "voice");
        Add(eps.FacePersonalStatus?.FaceEmotion?.Category, eps.FacePersonalStatus?.FaceEmotion?.GeneralAdjectival, "face");
        return string.Join(", ", parts);
    }

    // Verbalise the scene per the template: the visual objects and (if identified)
    // the audio objects populating it, each named by its Instance Identifier (its
    // type) and, when the Basic AV Scene is present, located at its spatial attitude.
    // Aligned audio+visual objects (the same entity seen and heard) are described
    // jointly. When no objects are identified, returns "".
    private string VerbaliseScene(Message message)
    {
        var visual = ReadIdList(message, _visualObjectIdsPort);
        var audio  = ReadIdList(message, _audioObjectIdsPort);
        if (visual.Count == 0 && audio.Count == 0) return "";

        bool haveScene = message.Ports.ContainsKey(_bmsPort) &&
                         !string.IsNullOrWhiteSpace(message.Ports[_bmsPort]);
        string located = haveScene ? " located at their respective spatial attitudes in the scene" : "";

        var clauses = new List<string>();
        if (visual.Count > 0)
            clauses.Add($"the following visual objects{located}: {string.Join(", ", visual.Select(DescribeType))}");
        if (audio.Count > 0)
            clauses.Add($"the following audio objects{located}: {string.Join(", ", audio.Select(DescribeType))}");
        // (Aligned audio+visual objects would be merged into joint descriptions when the
        //  BMS AlignedMMObjects declares them; the app supplies the alignment.)
        return string.Join(", and ", clauses);
    }

    // Describe an object's type from its Instance Identifier, hedging when the
    // identification is uncertain - a low top confidence, or two leading candidates
    // of similar confidence ("possibly a person, or perhaps a mannequin").
    private static string DescribeType(InstanceIdentifier iid)
    {
        var cands = (iid.InstanceIdentifierData ?? new List<InstanceCandidate>())
            .Where(c => !string.IsNullOrWhiteSpace(c.InstanceLabel))
            .OrderByDescending(c => c.LabelConfidenceLevel)
            .ToList();
        if (cands.Count == 0) return "an unidentified object";

        var top = cands[0];
        double topConf = top.LabelConfidenceLevel;
        if (cands.Count >= 2)
        {
            var second = cands[1];
            if (topConf < 0.5 || (topConf - second.LabelConfidenceLevel) < 0.15)
                return $"possibly {top.InstanceLabel}, or perhaps {second.InstanceLabel}";
        }
        if (topConf < 0.5) return $"possibly {top.InstanceLabel}";
        return top.InstanceLabel!;
    }

    // Read an OSD-IID port whose payload is a JSON array of Instance Identifiers
    // (the VII / ASI output list), or a single Instance Identifier, or empty.
    private static List<InstanceIdentifier> ReadIdList(Message message, string port)
    {
        var list = new List<InstanceIdentifier>();
        if (!message.Ports.TryGetValue(port, out var json) || string.IsNullOrWhiteSpace(json))
            return list;
        var trimmed = json.TrimStart();
        if (trimmed.StartsWith("["))
        {
            var arr = MpaiJson.FromJson<List<InstanceIdentifier>>(json);
            if (arr is not null) list.AddRange(arr);
        }
        else
        {
            var one = MpaiJson.FromJson<InstanceIdentifier>(json);
            if (one is not null) list.Add(one);
        }
        return list;
    }

    private static string? ReadText(Message message, string port)
    {
        if (!message.Ports.TryGetValue(port, out var json) || string.IsNullOrWhiteSpace(json)) return null;
        return MpaiJson.FromJson<BasicTextObject>(json)?.GetText();
    }

    private static string ReadInstanceLabel(Message message, string port)
    {
        if (!message.Ports.TryGetValue(port, out var json) || string.IsNullOrWhiteSpace(json)) return "";
        var iid = MpaiJson.FromJson<InstanceIdentifier>(json);
        return iid?.InstanceIdentifierData?.FirstOrDefault()?.InstanceLabel ?? "";
    }

    private static T? Read<T>(Message message, string port) where T : class
    {
        if (!message.Ports.TryGetValue(port, out var json) || string.IsNullOrWhiteSpace(json)) return null;
        return MpaiJson.FromJson<T>(json);
    }

    // Parse the LLM's JSON reply; fall back gracefully to plain text if it did not
    // return clean JSON.
    private static (string response, string emotion, string attitude, string summary) ParseReply(string reply, string userText)
    {
        string emotion = "NEUTRAL", attitude = "neutral", summary = "";
        string? response = null;

        var cleaned = StripFences(reply);
        var obj = FirstBalancedObject(cleaned);
        if (obj is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(obj);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("response", out var r) && r.ValueKind == JsonValueKind.String)
                        response = r.GetString();
                    if (root.TryGetProperty("emotion", out var e) && e.ValueKind == JsonValueKind.String)
                        emotion = e.GetString() ?? emotion;
                    if (root.TryGetProperty("attitude", out var a) && a.ValueKind == JsonValueKind.String)
                        attitude = a.GetString() ?? attitude;
                    if (root.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String)
                        summary = s.GetString() ?? summary;
                }
            }
            catch { /* fall through to a safe reply below */ }
        }

        // The spoken channel must NEVER contain JSON. If we didn't get a clean
        // "response" string, use the non-JSON remainder of the model output; if
        // that still looks like JSON, use a safe neutral line.
        if (string.IsNullOrWhiteSpace(response))
        {
            var stripped = RemoveJsonBlocks(cleaned).Trim();
            response = LooksLikeJson(stripped) || string.IsNullOrWhiteSpace(stripped)
                ? "I'm sorry, could you say that again?"
                : stripped;
        }

        return (Sanitise(response!), emotion, attitude, summary);
    }

    // Remove ```json ... ``` (or ``` ... ```) fences, keeping the inner content.
    private static string StripFences(string s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        s = System.Text.RegularExpressions.Regex.Replace(s, "```[a-zA-Z]*", "");
        return s.Replace("```", "").Trim();
    }

    // Return the first balanced {...} object substring, or null. Handles nested
    // braces and braces inside strings.
    private static string? FirstBalancedObject(string s)
    {
        int start = s.IndexOf('{');
        if (start < 0) return null;
        int depth = 0; bool inStr = false; bool esc = false;
        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') { inStr = true; continue; }
            if (c == '{') depth++;
            else if (c == '}') { depth--; if (depth == 0) return s.Substring(start, i - start + 1); }
        }
        return null;
    }

    // Delete any balanced {...} blocks from the text (used to recover a plain-text
    // reply when the model wrapped everything, or added prose around JSON).
    private static string RemoveJsonBlocks(string s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        string prev;
        do { prev = s; var obj = FirstBalancedObject(s); if (obj is null) break; s = s.Replace(obj, " "); }
        while (s != prev);
        return s;
    }

    private static bool LooksLikeJson(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.TrimStart();
        return t.StartsWith("{") || t.StartsWith("[") ||
              System.Text.RegularExpressions.Regex.IsMatch(s, @"(?i)""?(response|emotion|attitude|summary)""?\s*:");
    }

    // Strip any emotion/attitude labels the model leaked into the spoken response.
    // A weak model sometimes appends its structured fields as prose ("... Emotion:
    // happy, Attitude: friendly"); those must drive the face, never be spoken.
    private static string Sanitise(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return response;
        // Cut off at the first occurrence of a leaked label.
        var cut = System.Text.RegularExpressions.Regex.Match(
            response,
            @"(?i)[\s,;.\-]*(emotion|attitude|summary)\s*[:=]",
            System.Text.RegularExpressions.RegexOptions.None);
        if (cut.Success) response = response.Substring(0, cut.Index);
        // Remove any stray trailing JSON-ish braces/quotes.
        response = response.Trim().Trim('{', '}', '[', ']', '"', ',', ' ');
        return response.Trim();
    }
    // Build the machine's Personal Status from the LLM's stated emotion + attitude.
    // Carried as a Text modality PS inside the Entity Personal Status; PAF-PDR will
    // de-multiplex it to speech/face/gesture for avatar rendering.
    private static EntityPersonalStatus MachinePersonalStatus(string emotion, string attitude)
    {
        FactorLabel emo = emotion.ToUpperInvariant() switch
        {
            "HAPPINESS" => FactorLabel.Of("HAPPINESS", "happy", null, 0.8),
            "SADNESS"   => FactorLabel.Of("SADNESS", "sad", null, 0.8),
            "ANGER"     => FactorLabel.Of("ANGER", "angry", null, 0.8),
            "FEAR"      => FactorLabel.Of("FEAR", "fearful/scared", null, 0.8),
            "CALMNESS"  => FactorLabel.Of("CALMNESS", "calm", null, 0.8),
            _           => FactorLabel.Of("CALMNESS", "calm", null, 0.5)
        };
        SocialAttitude? att = attitude.ToLowerInvariant() switch
        {
            "respectful" => SocialAttitude.Of(FactorLabel.Of("SOCIAL RANK", "respectful", null, 0.8)),
            "friendly"   => SocialAttitude.Of(FactorLabel.Of("ACCEPTANCE", "friendly", null, 0.8)),
            "confident"  => SocialAttitude.Of(FactorLabel.Of("SOCIAL DOMINANCE/CONFIDENCE", "confident", null, 0.8)),
            _            => null
        };
        return new EntityPersonalStatus
        {
            TextPersonalStatus = new TextPersonalStatus
            {
                TextEmotion        = Emotion.Of(emo),
                TextSocialAttitude = att
            },
            FacePersonalStatus = new FacePersonalStatus
            {
                FaceEmotion = Emotion.Of(emo)
            },
            SpeechPersonalStatus = new SpeechPersonalStatus
            {
                SpeechEmotion = Emotion.Of(emo)
            }
        };
    }
}
