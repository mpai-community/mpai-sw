using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

using AIF.Controller;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Paf.Gfd;

// PAF-GFD-V1.6 - Generative Face Description, as an AIF IAimProcessor.
//
// Produces the Machine Face Descriptors - the whole facial animation the CAV
// displays - from three inputs:
//   - Face Personal Status : the machine's Emotion -> expression Action Units (EM-FACS),
//                            held across the utterance.
//   - Text Object          : the words -> phonemes (via espeak-ng) -> viseme mouth
//                            shapes (the lip part of the animation).
//   - Machine Speech        : the synthesised speech -> the utterance duration and an
//                            amplitude envelope that times and gates the mouth.
// The output is a Face Descriptors Object whose FaceDescriptorsData is a TIMELINE: a
// sequence of frames, each a SimpleTime + the Action Unit weights for that instant
// (expression merged with the frame's viseme, gated by the speech amplitude). The
// timing lives in the FDO structure (Time per frame), format-independent and
// interoperable - not buried in an opaque payload.
public sealed class GfdAimProcessor : IAimProcessor
{
    private const double FrameSeconds = 0.045;   // ~22 fps

    private readonly string _instanceId;
    private readonly string _facePort;    // MMC-FPS
    private readonly string _textPort;    // OSD-BTO
    private readonly string _speechPort;  // OSD-BSO
    private readonly string _outPort;     // PAF-FDO

    public GfdAimProcessor(string instanceId, AimPortReader ports)
    {
        _instanceId = instanceId;
        _facePort   = ports.Input("MMC-FPS-V2.5");
        _textPort   = ports.Input("OSD-BTO-V1.5");
        _speechPort = ports.Input("OSD-BSO-V1.5");
        _outPort    = ports.Output("PAF-FDO-V1.6");
    }

    public string InstanceId => _instanceId;

    public Task<Message> ProcessAsync(Message message)
    {
        // Expression from Face Personal Status (emotion -> AUs), held across the utterance.
        FaceActionUnits expression = FaceActionUnits.Of(new Dictionary<ActionUnit, double>());
        if (message.Ports.TryGetValue(_facePort, out var fpsJson) && !string.IsNullOrWhiteSpace(fpsJson))
        {
            var fps = MpaiJson.FromJson<FacePersonalStatus>(fpsJson);
            string? category = fps?.FaceEmotion?.Category;
            double intensity = fps?.FaceEmotion?.Degree ?? 0.6;
            if (category is null && fps?.FaceCognitiveState?.Category is { } cog)
            { category = cog; intensity = fps.FaceCognitiveState.Degree ?? 0.6; }
            expression = EmFacs.ToActionUnits(category, intensity);
        }

        // Words -> phonemes -> visemes (the lip shapes to move through).
        string text = ReadText(message);
        var visemes = Phonemize(text).Select(Mpai.Core.OSD.Visemes.FromPhoneme).ToList();
        if (visemes.Count == 0) visemes.Add(Viseme.Neutral);

        // Speech -> duration + amplitude envelope (times and gates the mouth).
        var (durationSeconds, envelope) = SpeechEnvelope(message);
        if (durationSeconds <= 0) durationSeconds = Math.Max(0.6, visemes.Count * 0.09);

        int frameCount = Math.Max(1, (int)Math.Round(durationSeconds / FrameSeconds));
        var frames = new List<FaceDescriptorsDataItem>(frameCount);

        for (int f = 0; f < frameCount; f++)
        {
            double t = f * FrameSeconds;
            // Which viseme this frame is on (visemes spread evenly across the utterance).
            int vi = (int)(f * (double)visemes.Count / frameCount);
            vi = Math.Clamp(vi, 0, visemes.Count - 1);
            double gate = SampleEnvelope(envelope, (double)f / frameCount);   // 0..1 mouth activity

            var viseme = Mpai.Core.OSD.Visemes.ToActionUnits(visemes[vi]);
            var gatedViseme = Scale(viseme, gate);
            var frameAus = expression.MergedWith(gatedViseme);

            frames.Add(new FaceDescriptorsDataItem
            {
                Time = SimpleTimeAt(t),
                Data = MpaiJson.ToJson(frameAus)
            });
        }

        var fdo = new FaceDescriptorsObject
        {
            FaceDescriptorsObjectID = Guid.NewGuid().ToString(),
            FaceDescriptorsObjectTime = SimpleTimeAt(0),
            FaceDescriptorsData = frames,
            FaceDescriptorsQualifier = FaceDescriptorsQualifier.For("FACS-AU"),
            DescrMetadata = $"expression + {visemes.Count} visemes over {durationSeconds:F2}s, {frameCount} frames"
        };

        return Task.FromResult(new Message
        {
            MessageId = message.MessageId,
            MessageType = message.MessageType,
            Ports = new Dictionary<string, string> { [_outPort] = MpaiJson.ToJson(fdo) }
        });
    }

    private string ReadText(Message message)
    {
        if (!message.Ports.TryGetValue(_textPort, out var json) || string.IsNullOrWhiteSpace(json))
            return "";
        try { return MpaiJson.FromJson<BasicTextObject>(json)?.GetText() ?? ""; }
        catch { return ""; }
    }

    private FaceActionUnits Scale(FaceActionUnits a, double k)
        => FaceActionUnits.Of(a.ActionUnits.ToDictionary(
            kv => Enum.Parse<ActionUnit>(kv.Key), kv => kv.Value * Math.Clamp(k, 0, 1)));

    // ---- espeak-ng phonemization (Text -> phoneme tokens). Piper depends on espeak-ng. ----
    private static IEnumerable<string> Phonemize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        try
        {
            var psi = new ProcessStartInfo("espeak-ng", $"-q --ipa=1 \"{text.Replace("\"", "")}\"")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p is null) return FallbackGraphemes(text);
            string ipa = p.StandardOutput.ReadToEnd();
            p.WaitForExit(4000);
            var toks = ipa.Where(ch => !char.IsWhiteSpace(ch)).Select(ch => ch.ToString()).ToList();
            return toks.Count > 0 ? toks : FallbackGraphemes(text);
        }
        catch
        {
            // espeak-ng not available - fall back to letters (coarse but keeps the mouth moving).
            return FallbackGraphemes(text);
        }
    }

    private static IEnumerable<string> FallbackGraphemes(string text)
        => text.Where(char.IsLetter).Select(c => c.ToString());

    // ---- Minimal WAV amplitude envelope (no NAudio; portable). Returns duration + per-window RMS. ----
    private (double durationSeconds, double[] envelope) SpeechEnvelope(Message message)
    {
        if (!message.Ports.TryGetValue(_speechPort, out var json) || string.IsNullOrWhiteSpace(json))
            return (0, Array.Empty<double>());
        byte[] wav;
        try { wav = MpaiJson.FromJson<BasicSpeechObject>(json)?.Data ?? Array.Empty<byte>(); }
        catch { return (0, Array.Empty<double>()); }
        if (wav.Length < 44) return (0, Array.Empty<double>());

        // Parse a canonical PCM WAV header (enough for Piper's 16-bit mono/stereo output).
        int channels   = BitConverter.ToInt16(wav, 22);
        int sampleRate  = BitConverter.ToInt32(wav, 24);
        int bits        = BitConverter.ToInt16(wav, 34);
        if (channels <= 0 || sampleRate <= 0 || bits != 16) return (0, Array.Empty<double>());

        // Find the 'data' chunk.
        int pos = 12;
        int dataOffset = 44, dataLen = wav.Length - 44;
        while (pos + 8 <= wav.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
            int len = BitConverter.ToInt32(wav, pos + 4);
            if (id == "data") { dataOffset = pos + 8; dataLen = Math.Min(len, wav.Length - dataOffset); break; }
            pos += 8 + len + (len & 1);
        }

        int bytesPerSample = 2 * channels;
        int sampleCount = dataLen / bytesPerSample;
        double duration = (double)sampleCount / sampleRate;

        int windows = Math.Max(1, (int)Math.Round(duration / FrameSeconds));
        var env = new double[windows];
        int per = Math.Max(1, sampleCount / windows);
        for (int w = 0; w < windows; w++)
        {
            double sum = 0; int n = 0;
            for (int s = w * per; s < (w + 1) * per && s < sampleCount; s++)
            {
                short v = BitConverter.ToInt16(wav, dataOffset + s * bytesPerSample);
                sum += (double)v * v; n++;
            }
            env[w] = n > 0 ? Math.Sqrt(sum / n) / 32768.0 : 0;
        }
        // Normalise the envelope to 0..1.
        double max = env.DefaultIfEmpty(0).Max();
        if (max > 1e-6) for (int i = 0; i < env.Length; i++) env[i] = Math.Min(1, env[i] / max * 1.4);
        return (duration, env);
    }

    private static double SampleEnvelope(double[] env, double frac)
    {
        if (env.Length == 0) return 0.7;   // no audio -> assume speaking
        int i = Math.Clamp((int)(frac * env.Length), 0, env.Length - 1);
        return env[i];
    }

    private static SimpleTime SimpleTimeAt(double seconds) => new()
    {
        SimpleTimeID = Guid.NewGuid().ToString(),
        SimpleTimeData = { new TimeSegment { FlagsByte = 1, StartTime = seconds, EndTime = seconds, TimeType = true, TimeUnit = "00" } }
    };
}
