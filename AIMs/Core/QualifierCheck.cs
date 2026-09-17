using System;
using System.Collections.Generic;

using Mpai.Core.OSD;

namespace Mpai.Core;

// DOES EVERY OBJECT SAY WHAT ITS DATA IS? Asked of every Object produced by every
// AIM, on every run.
//
// Levels 1 to 3 bind the code that can be bound: a Qualifier is required at
// construction, it must state a format, and a consumer takes the Object rather
// than its bytes. None of that reaches an AIM that builds its Object with an
// object initialiser, which most of them do, nor an AIM whose Qualifier describes
// something other than the Data it accompanies.
//
// So this runs instead of a test. A test that walks every AIM needs the models -
// Whisper, BLIP, Piper - and would be slow, fragile, and run rarely. Every Object
// in every Module already passes through the executor on its way between AIMs;
// checking there costs a parse and catches whatever the applications actually
// exercise.
//
// IT REPORTS, IT DOES NOT REFUSE. A check that stopped a Module would turn a
// quiet defect into an outage, and the defects it finds are months old. Reporting
// is what was missing, not enforcement.
public static class QualifierCheck
{
    // One complaint per AIM and Data Type per process. An AIM producing a datum
    // per animation frame would otherwise fill the log with one finding.
    private static readonly HashSet<string> Reported = new();

    public static void Inspect(
        string aimName,
        string dataType,
        string json)
    {
        if (string.IsNullOrWhiteSpace(dataType) || string.IsNullOrWhiteSpace(json))
            return;

        var complaint = Complain(dataType, json);
        if (complaint is null) return;

        lock (Reported)
        {
            if (!Reported.Add($"{aimName}|{dataType}")) return;
        }

        AimLog.Write(aimName, complaint);
    }

    // The complaint, or null when the Object states its format. Only Data Types
    // with a rule are examined; the rest are passed over rather than guessed at.
    public static string? Complain(
        string dataType,
        string json)
    {
        try
        {
            if (dataType.StartsWith("OSD-BSO", StringComparison.Ordinal))
            {
                var speech = MpaiJson.FromJson<BasicSpeechObject>(json);
                if (speech is null || speech.Data.Length == 0) return null;

                if (speech.SpeechQualifier is null)
                    return "produced a Speech Object with no Qualifier: nothing says what the bytes are.";

                if (!speech.SpeechQualifier.StatesFormat())
                    return "produced a Speech Object whose Qualifier states no format - " +
                           "no sampling frequency and precision, and no container.";
            }
            else if (dataType.StartsWith("OSD-BAO", StringComparison.Ordinal))
            {
                var audio = MpaiJson.FromJson<BasicAudioObject>(json);
                if (audio is null) return null;

                if (audio.AudioQualifier is null)
                    return "produced an Audio Object with no Qualifier: nothing says what the bytes are.";

                if (!audio.AudioQualifier.StatesFormat())
                    return "produced an Audio Object whose Qualifier states no format - " +
                           "no sampling frequency and precision, and no container.";
            }
            else if (dataType.StartsWith("OSD-BVO", StringComparison.Ordinal))
            {
                // Visual has no StatesFormat yet: an image container is
                // self-describing in a way raw samples are not, so the absence of a
                // declaration is less dangerous. Presence is still worth asking for.
                var visual = MpaiJson.FromJson<BasicVisualObject>(json);
                if (visual is null || visual.Data.Length == 0) return null;

                if (visual.VisualQualifier is null)
                    return "produced a Visual Object with no Qualifier.";
            }
        }
        catch
        {
            // A payload that will not parse is a different fault, reported
            // elsewhere. This check does not turn it into a second one.
        }

        return null;
    }
}