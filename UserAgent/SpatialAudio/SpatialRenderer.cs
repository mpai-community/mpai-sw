using System;
using System.Collections.Generic;
using System.Linq;

using Mpai.Core;

namespace Mpai.SpatialAudio;

// THE SPATIAL AUDIO RENDERER OF THE USER AGENT (the author, 2026/10/02: "ideally, an AOD
// is a spatial audio renderer"), on Steam Audio. A scene is heard from a User
// Point of View: each source - mono samples, a position and an orientation, a
// gain, a directional pattern - reaches the user attenuated by distance and by the
// air, by the pattern at the angle the user is seen from the source, and from its
// direction: binaural (an HRTF) for headphones, or panned onto a speaker layout.
//
// COORDINATES, as MPAI's Cartesian positions are read here: X to the right, Y
// ahead, Z up, in metres. An orientation's yaw turns about Z, positive to the left
// (counterclockwise seen from above), 0 facing ahead; its pitch raises the front.
// Steam Audio's own frame - X right, Y up, -Z ahead - is reached by (X, Z, -Y).
//
// Not rendered yet: the room's reverberation (the Scene part of the Acoustic
// Profile, or a Closed Space), the Doppler effect, and a source's roll.
public sealed class SpatialRenderer : IDisposable
{
    public enum Layout { Binaural, Mono, Stereo, Quad, Surround51, Surround71 }

    public sealed record Source(float[] Samples, double[] Position, double YawDeg, double PitchDeg, double Gain = 1, DirectionalPattern? Pattern = null);
    public sealed record User(double[] Position, double YawDeg, double PitchDeg);

    public const int Rate = 48000;
    private const int Frame = 1024;

    private IntPtr _context, _hrtf;
    private readonly Layout _layout;

    public int Channels => _layout switch { Layout.Mono => 1, Layout.Quad => 4, Layout.Surround51 => 6, Layout.Surround71 => 8, _ => 2 };

    public static bool Available(string folder) => SteamAudio.Present(folder);

    public SpatialRenderer(string steamAudioFolder, Layout layout = Layout.Binaural)
    {
        SteamAudio.Load(steamAudioFolder);
        _layout = layout;
        var cs = new SteamAudio.ContextSettings { Version = SteamAudio.Version };
        Check(SteamAudio.iplContextCreate(ref cs, out _context), "context");
        if (layout == Layout.Binaural)
        {
            var audio = Audio();
            var hs = new SteamAudio.HrtfSettings { Type = 0, Volume = 1, NormType = 0 };
            Check(SteamAudio.iplHRTFCreate(_context, ref audio, ref hs, out _hrtf), "HRTF");
        }
    }

    private static SteamAudio.AudioSettings Audio() => new() { SamplingRate = Rate, FrameSize = Frame };
    private static void Check(int status, string what) { if (status != 0) throw new InvalidOperationException($"Steam Audio: {what} failed ({status})."); }

    // MPAI (X right, Y ahead, Z up) to Steam Audio (X right, Y up, -Z ahead).
    private static SteamAudio.Vector3 Steam(double x, double y, double z) => new((float)x, (float)z, (float)-y);
    private static (double X, double Y, double Z) Ahead(double yawDeg, double pitchDeg)
    {
        double yaw = yawDeg * Math.PI / 180, pitch = pitchDeg * Math.PI / 180;
        return (-Math.Sin(yaw) * Math.Cos(pitch), Math.Cos(yaw) * Math.Cos(pitch), Math.Sin(pitch));
    }

    // Where the user is seen from the source: azimuth (positive to the source's
    // right) and elevation, in degrees, in the source's own frame.
    public static (double Azimuth, double Elevation) Seen(Source s, double[] user)
    {
        double dx = user[0] - s.Position[0], dy = user[1] - s.Position[1], dz = user[2] - s.Position[2];
        var n = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (n < 1e-9) return (0, 0);
        dx /= n; dy /= n; dz /= n;
        var f = Ahead(s.YawDeg, s.PitchDeg);
        var r = (X: f.Y * 1 - f.Z * 0, Y: f.Z * 0 - f.X * 1, Z: 0.0);   // ahead x up(0,0,1)
        var rn = Math.Sqrt(r.X * r.X + r.Y * r.Y); if (rn > 1e-9) r = (r.X / rn, r.Y / rn, 0);
        var az = Math.Atan2(dx * r.X + dy * r.Y, dx * f.X + dy * f.Y + dz * f.Z) * 180 / Math.PI;
        var el = Math.Asin(Math.Clamp(dz, -1, 1)) * 180 / Math.PI;
        return (az, el);
    }

    // The scene heard from the user, interleaved, Channels channels at Rate.
    public float[] Render(IReadOnlyList<Source> sources, User user)
    {
        var length = sources.Count == 0 ? 0 : sources.Max(s => s.Samples.Length);
        var frames = (length + Frame - 1) / Frame;
        var mix = new float[frames * Frame * Channels];
        var audio = Audio();
        var userPos = Steam(user.Position[0], user.Position[1], user.Position[2]);
        var ua = Ahead(user.YawDeg, user.PitchDeg);
        var userAhead = Steam(ua.X, ua.Y, ua.Z);
        var userUp = Steam(0, 0, 1);

        foreach (var s in sources)
        {
            IntPtr direct = IntPtr.Zero, spatial = IntPtr.Zero;
            var input = new SteamAudio.AudioBuffer(); var mid = new SteamAudio.AudioBuffer(); var output = new SteamAudio.AudioBuffer();
            try
            {
                var ds = new SteamAudio.DirectEffectSettings { NumChannels = 1 };
                Check(SteamAudio.iplDirectEffectCreate(_context, ref audio, ref ds, out direct), "direct effect");
                if (_layout == Layout.Binaural)
                {
                    var bs = new SteamAudio.BinauralEffectSettings { Hrtf = _hrtf };
                    Check(SteamAudio.iplBinauralEffectCreate(_context, ref audio, ref bs, out spatial), "binaural effect");
                }
                else
                {
                    var ps = new SteamAudio.PanningEffectSettings { SpeakerLayout = new SteamAudio.SpeakerLayout { Type = (int)_layout - 1 } };
                    Check(SteamAudio.iplPanningEffectCreate(_context, ref audio, ref ps, out spatial), "panning effect");
                }
                Check(SteamAudio.iplAudioBufferAllocate(_context, 1, Frame, ref input), "buffer");
                Check(SteamAudio.iplAudioBufferAllocate(_context, 1, Frame, ref mid), "buffer");
                Check(SteamAudio.iplAudioBufferAllocate(_context, Channels, Frame, ref output), "buffer");

                // The source does not move during the render: its parameters once.
                var pos = Steam(s.Position[0], s.Position[1], s.Position[2]);
                var dam = new SteamAudio.DistanceAttenuationModel { Type = 0 };
                var aam = new SteamAudio.AirAbsorptionModel { Type = 0 };
                var air = new float[3];
                SteamAudio.iplAirAbsorptionCalculate(_context, pos, userPos, ref aam, air);
                var (az, el) = Seen(s, user.Position);
                var dp = new SteamAudio.DirectEffectParams
                {
                    Flags = SteamAudio.DirectDistanceAttenuation | SteamAudio.DirectAirAbsorption | SteamAudio.DirectDirectivity,
                    DistanceAttenuation = SteamAudio.iplDistanceAttenuationCalculate(_context, pos, userPos, ref dam),
                    Air0 = air[0], Air1 = air[1], Air2 = air[2],
                    Directivity = (float)Math.Clamp(s.Pattern?.Gain(az, el) ?? 1, 0, 1)
                };
                var direction = SteamAudio.iplCalculateRelativeDirection(_context, pos, userPos, userAhead, userUp);

                var inFrame = new float[Frame];
                var outFrame = new float[Frame * Channels];
                for (var f = 0; f < frames; f++)
                {
                    Array.Clear(inFrame);
                    var n = Math.Max(0, Math.Min(Frame, s.Samples.Length - f * Frame));
                    for (var i = 0; i < n; i++) inFrame[i] = (float)(s.Samples[f * Frame + i] * s.Gain);
                    SteamAudio.iplAudioBufferDeinterleave(_context, inFrame, ref input);
                    SteamAudio.iplDirectEffectApply(direct, ref dp, ref input, ref mid);
                    if (_layout == Layout.Binaural)
                    {
                        var bp = new SteamAudio.BinauralEffectParams { Direction = direction, Interpolation = 1, SpatialBlend = 1, Hrtf = _hrtf };
                        SteamAudio.iplBinauralEffectApply(spatial, ref bp, ref mid, ref output);
                    }
                    else
                    {
                        var pp = new SteamAudio.PanningEffectParams { Direction = direction };
                        SteamAudio.iplPanningEffectApply(spatial, ref pp, ref mid, ref output);
                    }
                    SteamAudio.iplAudioBufferInterleave(_context, ref output, outFrame);
                    var at = f * Frame * Channels;
                    for (var i = 0; i < outFrame.Length; i++) mix[at + i] += outFrame[i];
                }
            }
            finally
            {
                if (input.Data != IntPtr.Zero) SteamAudio.iplAudioBufferFree(_context, ref input);
                if (mid.Data != IntPtr.Zero) SteamAudio.iplAudioBufferFree(_context, ref mid);
                if (output.Data != IntPtr.Zero) SteamAudio.iplAudioBufferFree(_context, ref output);
                if (direct != IntPtr.Zero) SteamAudio.iplDirectEffectRelease(ref direct);
                if (spatial != IntPtr.Zero)
                {
                    if (_layout == Layout.Binaural) SteamAudio.iplBinauralEffectRelease(ref spatial);
                    else SteamAudio.iplPanningEffectRelease(ref spatial);
                }
            }
        }
        return mix;
    }

    public void Dispose()
    {
        if (_hrtf != IntPtr.Zero) SteamAudio.iplHRTFRelease(ref _hrtf);
        if (_context != IntPtr.Zero) SteamAudio.iplContextRelease(ref _context);
    }
}

// A source's directional pattern, from the Acoustic Profile's DirectionalPatterns:
// a 2D Plot whose rows are [azimuth, elevation, gain in dB], the angles in degrees
// in the source's frame (azimuth positive to its right). The gain at a direction is
// the nearest row's; it is relative, 0 dB the loudest.
public sealed class DirectionalPattern
{
    private readonly (double Az, double El, double Db)[] _rows;
    public DirectionalPattern(IEnumerable<(double Az, double El, double Db)> rows) => _rows = rows.ToArray();

    public static DirectionalPattern? FromPlot(Plot? plot) =>
        plot?.Data?.TwoD is { Count: > 0 } rows
            ? new DirectionalPattern(rows.Where(r => r.Count >= 3).Select(r => (r[0], r[1], r[2])))
            : null;

    public double Gain(double azimuth, double elevation)
    {
        if (_rows.Length == 0) return 1;
        double Distance((double Az, double El, double Db) r)
        {
            double a1 = azimuth * Math.PI / 180, e1 = elevation * Math.PI / 180, a2 = r.Az * Math.PI / 180, e2 = r.El * Math.PI / 180;
            return Math.Acos(Math.Clamp(Math.Sin(e1) * Math.Sin(e2) + Math.Cos(e1) * Math.Cos(e2) * Math.Cos(a1 - a2), -1, 1));
        }
        var nearest = _rows.MinBy(Distance);
        return Math.Pow(10, Math.Min(0, nearest.Db) / 20);
    }
}
