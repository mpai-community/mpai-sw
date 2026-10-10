using System.Globalization;
using System.Text;

namespace Mpai.Paf.Gbd;

// A SPEAKING BODY, AS BVH. The motion of a speaking avatar's body for one utterance,
// as long as its speech, at 30 frames a second:
//   - at rest, always: breathing in the chest, a slow sway of the head, a slow shift of
//     the weight;
//   - while speaking: the hands come up in front of the body, and beat - a small down
//     stroke of the forearm and wrist - on the stressed syllables, the peaks of the
//     speech's loudness, the two hands taking turns, both on the strongest;
//   - from the text: a question ends with open palms; "yes", "sure", "certainly"
//     begin with nods; each sentence ends with a small nod;
//   - with an emotion: more and wider movement when happy, surprised or angry; less,
//     the chest and head lower, when sad.
//
// THE SKELETON (Skeleton below) is named as the common humanoid rigs (Hips, Spine,
// Spine1, Spine2, Neck, Head, Left/RightShoulder, Arm, ForeArm, Hand) and stands at rest
// with the arms down. Its axes are the body's: Y up, Z ahead, X to the avatar's left.
// Every rotation is an angle about those axes, in degrees, at that joint - an arm's X
// rotation negative raises it forward, its Z rotation takes the left arm out (positive)
// and the right arm out (negative); a forearm's X rotation negative bends the elbow, its
// Y rotation turns the palm; the spine's and head's X positive bend them forward, Y
// positive turns them left. A renderer applies them to its own rig as rotations about
// these axes from its own arms-down rest pose.
public static class BodyMotion
{
    public const double Fps = 30;

    public sealed record Result(string Bvh, int Frames, int Beats, int Questions, int Nods);

    private static readonly string[] Joints =
    [
        "Hips", "Spine", "Spine1", "Spine2", "Neck", "Head",
        "LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand",
        "RightShoulder", "RightArm", "RightForeArm", "RightHand"
    ];

    // The BVH hierarchy, in centimetres, arms down.
    private const string Skeleton =
@"HIERARCHY
ROOT Hips
{
  OFFSET 0.00 100.00 0.00
  CHANNELS 6 Xposition Yposition Zposition Zrotation Xrotation Yrotation
  JOINT Spine
  {
    OFFSET 0.00 10.00 0.00
    CHANNELS 3 Zrotation Xrotation Yrotation
    JOINT Spine1
    {
      OFFSET 0.00 10.00 0.00
      CHANNELS 3 Zrotation Xrotation Yrotation
      JOINT Spine2
      {
        OFFSET 0.00 12.00 0.00
        CHANNELS 3 Zrotation Xrotation Yrotation
        JOINT Neck
        {
          OFFSET 0.00 14.00 0.00
          CHANNELS 3 Zrotation Xrotation Yrotation
          JOINT Head
          {
            OFFSET 0.00 9.00 0.00
            CHANNELS 3 Zrotation Xrotation Yrotation
            End Site
            {
              OFFSET 0.00 18.00 0.00
            }
          }
        }
        JOINT LeftShoulder
        {
          OFFSET 4.00 11.00 0.00
          CHANNELS 3 Zrotation Xrotation Yrotation
          JOINT LeftArm
          {
            OFFSET 13.00 0.00 0.00
            CHANNELS 3 Zrotation Xrotation Yrotation
            JOINT LeftForeArm
            {
              OFFSET 0.00 -28.00 0.00
              CHANNELS 3 Zrotation Xrotation Yrotation
              JOINT LeftHand
              {
                OFFSET 0.00 -25.00 0.00
                CHANNELS 3 Zrotation Xrotation Yrotation
                End Site
                {
                  OFFSET 0.00 -18.00 0.00
                }
              }
            }
          }
        }
        JOINT RightShoulder
        {
          OFFSET -4.00 11.00 0.00
          CHANNELS 3 Zrotation Xrotation Yrotation
          JOINT RightArm
          {
            OFFSET -13.00 0.00 0.00
            CHANNELS 3 Zrotation Xrotation Yrotation
            JOINT RightForeArm
            {
              OFFSET 0.00 -28.00 0.00
              CHANNELS 3 Zrotation Xrotation Yrotation
              JOINT RightHand
              {
                OFFSET 0.00 -25.00 0.00
                CHANNELS 3 Zrotation Xrotation Yrotation
                End Site
                {
                  OFFSET 0.00 -18.00 0.00
                }
              }
            }
          }
        }
      }
    }
  }
}
";

    // How much a body moves, and how it holds itself, with each emotion.
    private static (double Energy, double Slump) Mood(string? emotion, double degree) => (emotion?.ToUpperInvariant()) switch
    {
        "HAPPINESS" or "JOY" => (1.0 + 0.4 * degree, 0),
        "SURPRISE" => (1.0 + 0.4 * degree, 0),
        "ANGER" => (1.0 + 0.5 * degree, 0),
        "SADNESS" => (1.0 - 0.5 * degree, 7 * degree),
        "FEAR" => (1.0 - 0.3 * degree, 3 * degree),
        _ => (0.85, 0)
    };

    private static double Smooth(double x) => x <= 0 ? 0 : x >= 1 ? 1 : x * x * (3 - 2 * x);

    // A raised-cosine bump, 1 at its centre, 0 beyond half a width either side.
    private static double Bump(double t, double centre, double width)
    {
        var d = Math.Abs(t - centre) / (width / 2);
        return d >= 1 ? 0 : 0.5 * (1 + Math.Cos(Math.PI * d));
    }

    // The stressed syllables: the peaks of the loudness, at least 0.4 s apart.
    public static List<(double Time, double Strength)> Beats(double duration, double[] envelope)
    {
        var beats = new List<(double, double)>();
        if (envelope.Length < 3 || duration <= 0) return beats;
        var step = duration / envelope.Length;
        var reach = Math.Max(1, (int)Math.Round(0.15 / step));
        for (var i = 1; i < envelope.Length - 1; i++)
        {
            if (envelope[i] < 0.55) continue;
            var peak = true;
            for (var k = Math.Max(0, i - reach); k <= Math.Min(envelope.Length - 1, i + reach) && peak; k++)
                if (envelope[k] > envelope[i]) peak = false;
            var t = (i + 0.5) * step;
            if (peak && (beats.Count == 0 || t - beats[^1].Item1 >= 0.4)) beats.Add((t, envelope[i]));
        }
        return beats;
    }

    public static Result Generate(string text, double duration, double[] envelope, string? emotion = null, double degree = 0.6)
    {
        duration = Math.Max(duration, 0);
        var frames = Math.Max(1, (int)Math.Ceiling(duration * Fps) + 1);
        var (energy, slump) = Mood(emotion, Math.Clamp(degree, 0, 1));
        var beats = Beats(duration, envelope);

        // Where the sentences end, in time: their characters spread over the speech.
        text ??= "";
        var len = Math.Max(1, text.Length);
        var questions = new List<double>();
        var ends = new List<double>();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('?' or '.' or '!')) continue;
            var t = duration * (i + 1) / len;
            if (text[i] == '?') questions.Add(t); else ends.Add(t);
        }
        var words = text.ToLowerInvariant().Split([' ', ',', '.', '!', '?', ';', ':'], StringSplitOptions.RemoveEmptyEntries);
        var assent = words.Length > 0 && words[0] is "yes" or "yeah" or "sure" or "certainly" or "of" && (words[0] != "of" || words.Length > 1 && words[1] == "course");

        var sb = new StringBuilder(Skeleton);
        sb.Append("MOTION\n").Append($"Frames: {frames}\n").Append(string.Create(CultureInfo.InvariantCulture, $"Frame Time: {1 / Fps:0.######}\n"));
        var r = new Dictionary<string, (double Z, double X, double Y)>();
        for (var f = 0; f < frames; f++)
        {
            var t = f / Fps;
            foreach (var j in Joints) r[j] = (0, 0, 0);

            // Speaking: the hands up in front, eased in over 0.4 s and out over 0.5 s.
            var talk = duration <= 0 ? 0 : Smooth(t / 0.4) * Smooth((duration - t) / 0.5);
            var lift = 18 * talk * Math.Min(energy, 1.2);
            var elbow = 70 * talk;

            // Beats: the hands take turns; the strongest are both hands.
            double left = 0, right = 0;
            for (var b = 0; b < beats.Count; b++)
            {
                var w = Bump(t, beats[b].Time, 0.35) * beats[b].Strength * energy * talk;
                if (beats[b].Strength > 0.85 || b % 2 == 0) left += w;
                if (beats[b].Strength > 0.85 || b % 2 == 1) right += w;
            }

            // A question: the palms open, the arms a little out, for the last second.
            double open = 0;
            foreach (var q in questions) open = Math.Max(open, Smooth((t - (q - 1.0)) / 0.4) * Smooth((q + 0.4 - t) / 0.3));
            open *= talk > 0 ? 1 : 0;

            // Nods: assent at the start, a small one at each sentence's end.
            double nod = 0;
            if (assent) nod += 12 * Math.Max(0, Math.Sin(2 * Math.PI * t / 0.45)) * (t < 0.9 ? 1 : 0);
            foreach (var e in ends) nod += 6 * Bump(t, e, 0.5);

            // At rest, always: breathing, a sway of the head, a shift of the weight.
            var breath = 1.2 * Math.Sin(2 * Math.PI * 0.25 * t);
            var swayY = 2.0 * Math.Sin(2 * Math.PI * 0.11 * t);
            var swayX = 1.5 * Math.Sin(2 * Math.PI * 0.17 * t + 1);
            var weight = 1.5 * Math.Sin(2 * Math.PI * 0.08 * t);

            r["Hips"] = (weight, 0, 0);
            r["Spine1"] = (0, breath + slump * 0.5, 0);
            r["Spine2"] = (0, slump * 0.5, 0);
            r["Neck"] = (0, slump * 0.3, 0);
            r["Head"] = (0, swayX + nod + slump * 0.6, swayY);
            r["LeftArm"] = (8 * talk + 15 * open, -(lift + 6 * left), 0);
            r["RightArm"] = (-(8 * talk + 15 * open), -(lift + 6 * right), 0);
            r["LeftForeArm"] = (0, -(elbow + 12 * left + 15 * open), 60 * open);
            r["RightForeArm"] = (0, -(elbow + 12 * right + 15 * open), -60 * open);
            r["LeftHand"] = (0, -10 * left, 0);
            r["RightHand"] = (0, -10 * right, 0);

            var line = new StringBuilder();
            line.Append(string.Create(CultureInfo.InvariantCulture, $"{weight * 0.6:0.###} {100.0:0.###} {0.0:0.###}"));
            foreach (var j in Joints)
            {
                var (z, x, y) = r[j];
                line.Append(string.Create(CultureInfo.InvariantCulture, $" {z:0.###} {x:0.###} {y:0.###}"));
            }
            sb.Append(line).Append('\n');
        }
        return new Result(sb.ToString(), frames, beats.Count, questions.Count, (assent ? 1 : 0) + ends.Count);
    }
}
