using System;
using System.Collections.Generic;
using System.Linq;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.SpatialAudio;

// FROM AN AUDIO SCENE TO WHAT THE RENDERER TAKES. A Basic Audio Scene places Basic
// Audio Objects; an Audio Scene places Basic and full Audio Objects and sub-scenes,
// each with its Space-Time relative to what holds it. Every Basic Audio Object is a
// source: its sound (the WAV it carries, as mono at the renderer's rate), where it
// is in the scene (the placements composed: positions added, yaws added), its
// directional pattern (its Acoustic Profile), and the user it is heard by - the
// member's UserPoV, else the scene's, else the origin facing ahead.
public static class SceneAudio
{
    public sealed record Member(BasicAudioObject Object, double[] Position, double YawDeg, double PitchDeg, PointOfView? User);

    private static (double[] Position, double Yaw, double Pitch) Of(SpaceTime? st)
    {
        var sa = st?.SpatialAttitude1;
        var p = sa?.Position?.CartPosition is { Length: >= 3 } c ? new[] { c[0], c[1], c[2] } : new double[3];
        var e = sa?.Orientation?.EulerAngles is { Length: >= 3 } a ? a : new double[3];   // roll, pitch, yaw
        return (p, e[2], e[1]);
    }

    private static (double[] Position, double Yaw, double Pitch) Compose((double[] P, double Yaw, double Pitch) outer, (double[] P, double Yaw, double Pitch) inner)
    {
        // The inner position turned by the outer yaw, then moved by the outer position.
        var y = outer.Yaw * Math.PI / 180;
        double x = inner.P[0] * Math.Cos(y) - inner.P[1] * Math.Sin(y), yy = inner.P[0] * Math.Sin(y) + inner.P[1] * Math.Cos(y);
        return ([outer.P[0] + x, outer.P[1] + yy, outer.P[2] + inner.P[2]], outer.Yaw + inner.Yaw, outer.Pitch + inner.Pitch);
    }

    private static readonly (double[] P, double Yaw, double Pitch) Here = (new double[3], 0, 0);

    public static IEnumerable<Member> Members(BasicAudioSceneDescriptors scene) => Members(scene, Here, null);

    private static IEnumerable<Member> Members(BasicAudioSceneDescriptors scene, (double[] P, double Yaw, double Pitch) frame, PointOfView? outerUser)
    {
        var user = scene.UserPoV ?? outerUser;
        foreach (var e in scene.BasicAudioSceneDescriptorsEntries)
            if (e.AudioObjectIDOrAudioObject is { } bao)
            {
                var (p, yaw, pitch) = Compose(frame, Of(e.AudioObjectSpaceTime));
                yield return new Member(bao, p, yaw, pitch, e.UserPoV ?? user);
            }
    }

    public static IEnumerable<Member> Members(AudioSceneDescriptors scene) => Members(scene, Here, null);

    private static IEnumerable<Member> Members(AudioSceneDescriptors scene, (double[] P, double Yaw, double Pitch) frame, PointOfView? outerUser)
    {
        var user = scene.UserPoV ?? outerUser;
        foreach (var e in scene.AudioObjects ?? [])
            if (e.ObjectIDOrObject is { } auo)
                foreach (var m in Members(auo, Compose(frame, Of(e.AudioObjectSpaceTime)), user)) yield return m;
        foreach (var s in scene.SubAudioScenes ?? [])
            if (s.SubAudioSceneIDOrSubAudioScene is { } sub)
                foreach (var m in Members(sub, Compose(frame, Of(s.SubAudioSceneSpaceTime)), user)) yield return m;
    }

    // An Audio Object heard on its own, from its UserPoV.
    public static IEnumerable<Member> Members(AudioObject o) => Members(o, Here, o.UserPoV);

    private static IEnumerable<Member> Members(AudioObject o, (double[] P, double Yaw, double Pitch) frame, PointOfView? user)
    {
        var here = Compose(frame, Of(o.AudioObjectSpaceTime));
        foreach (var b in o.BasicAudioObjects ?? [])
            if (b.BAObjectIDOrBAObject is { } bao)
            {
                var (p, yaw, pitch) = Compose(here, Of(b.BasicAudioObjectSpaceTime));
                yield return new Member(bao, p, yaw, pitch, user);
            }
        foreach (var s in o.SubAudioObjects ?? [])
            if (s.SubAObjectIDOrSubAObject is { } sub)
                foreach (var m in Members(sub, Compose(here, Of(s.SubAudioObjectSpaceTime)), user)) yield return m;
    }

    // The user a member is heard by, as the renderer takes it.
    public static SpatialRenderer.User UserOf(PointOfView? pov) =>
        pov is null
            ? new SpatialRenderer.User([0, 0, 0], 0, 0)
            : new SpatialRenderer.User(pov.CartPosition is { Length: >= 3 } c ? c : new double[3],
                                       pov.Orientation is { Length: >= 3 } o ? o[2] : 0,
                                       pov.Orientation is { Length: >= 3 } o2 ? o2[1] : 0);

    // A WAV (PCM 16 or 32 bit, IEEE float 32 bit; any channels) as mono at rate.
    public static float[] Mono(byte[] wav, int rate)
    {
        if (wav.Length < 44 || System.Text.Encoding.ASCII.GetString(wav, 0, 4) != "RIFF") return [];
        int channels = 1, sampleRate = rate, bits = 16, format = 1, pos = 12, offset = -1, length = 0;
        while (pos + 8 <= wav.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
            var len = BitConverter.ToInt32(wav, pos + 4);
            if (id == "fmt ") { format = BitConverter.ToInt16(wav, pos + 8); channels = BitConverter.ToInt16(wav, pos + 10); sampleRate = BitConverter.ToInt32(wav, pos + 12); bits = BitConverter.ToInt16(wav, pos + 22); }
            if (id == "data") { offset = pos + 8; length = Math.Min(len, wav.Length - offset); break; }
            pos += 8 + len + (len & 1);
        }
        if (offset < 0 || channels <= 0) return [];
        var bytes = bits / 8; var frames = length / (bytes * channels);
        var mono = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            double sum = 0;
            for (var c = 0; c < channels; c++)
            {
                var at = offset + (f * channels + c) * bytes;
                sum += format == 3 && bits == 32 ? BitConverter.ToSingle(wav, at)
                     : bits == 32 ? BitConverter.ToInt32(wav, at) / 2147483648.0
                     : BitConverter.ToInt16(wav, at) / 32768.0;
            }
            mono[f] = (float)(sum / channels);
        }
        if (sampleRate == rate) return mono;
        var outLength = (int)((long)mono.Length * rate / sampleRate);
        var resampled = new float[outLength];
        for (var i = 0; i < outLength; i++)
        {
            var x = (double)i * sampleRate / rate; var j = (int)x; var t = x - j;
            resampled[i] = (float)(mono[Math.Min(j, mono.Length - 1)] * (1 - t) + mono[Math.Min(j + 1, mono.Length - 1)] * t);
        }
        return resampled;
    }

    // Interleaved samples as a 16-bit PCM WAV.
    public static byte[] Wav(float[] interleaved, int channels, int rate)
    {
        var data = new byte[interleaved.Length * 2];
        for (var i = 0; i < interleaved.Length; i++)
            BitConverter.GetBytes((short)Math.Clamp(Math.Round(interleaved[i] * 32767.0), short.MinValue, short.MaxValue)).CopyTo(data, i * 2);
        var wav = new byte[44 + data.Length];
        void S(int at, string s) => System.Text.Encoding.ASCII.GetBytes(s).CopyTo(wav, at);
        void I(int at, int v) => BitConverter.GetBytes(v).CopyTo(wav, at);
        void H(int at, short v) => BitConverter.GetBytes(v).CopyTo(wav, at);
        S(0, "RIFF"); I(4, 36 + data.Length); S(8, "WAVE"); S(12, "fmt "); I(16, 16); H(20, 1); H(22, (short)channels);
        I(24, rate); I(28, rate * channels * 2); H(32, (short)(channels * 2)); H(34, 16); S(36, "data"); I(40, data.Length);
        data.CopyTo(wav, 44);
        return wav;
    }
}
