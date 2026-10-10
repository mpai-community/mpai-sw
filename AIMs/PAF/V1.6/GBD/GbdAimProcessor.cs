using AIF.Controller;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Paf.Gbd;

// PAF-GBD-V1.6 - Generative Body Description, as an AIF IAimProcessor: the body's
// counterpart of GFD. It produces the Machine Body Descriptors - the body animation of
// a speaking avatar, for one utterance - from:
//   - Text Object        : the words - a question ends with open palms, assent begins
//                          with nods, a sentence ends with a nod;
//   - Machine Speech     : the synthesised speech - how long the body speaks, and the
//                          loudness whose peaks the hands beat on;
//   - Body Personal Status (optional): the emotion - how much the body moves and how it
//                          holds itself; absent, a neutral posture.
// The Body Descriptors Object carries the motion as BVH (BodyDescriptorsContentFormats:
// "BVH") on the skeleton and axes BodyMotion describes.
public sealed class GbdAimProcessor : IAimProcessor
{
    private const double EnvelopeSeconds = 0.045;   // the loudness measured as GFD measures it

    private readonly string _statusPort;   // MMC-BPS, "" when the L3 has none
    private readonly string _textPort;     // OSD-BTO
    private readonly string _speechPort;   // OSD-BSO
    private readonly string _outPort;      // PAF-BDO

    public string InstanceId { get; }

    public GbdAimProcessor(string instanceId, AimPortReader ports)
    {
        InstanceId  = instanceId;
        _statusPort = ports.InputOrDefault("MMC-BPS-V2.5", "");
        _textPort   = ports.Input("OSD-BTO-V1.5");
        _speechPort = ports.Input("OSD-BSO-V1.5");
        _outPort    = ports.Output("PAF-BDO-V1.6");
    }

    public Task<Message> ProcessAsync(Message message)
    {
        string? emotion = null; double degree = 0.6;
        if (_statusPort.Length > 0 && message.Ports.TryGetValue(_statusPort, out var gps) && !string.IsNullOrWhiteSpace(gps))
        {
            var status = MpaiJson.FromJson<BodyPersonalStatus>(gps);
            emotion = status?.BodyEmotion?.Category;
            degree = status?.BodyEmotion?.Degree ?? 0.6;
        }
        var text = message.Ports.TryGetValue(_textPort, out var tj) && !string.IsNullOrWhiteSpace(tj)
            ? MpaiJson.FromJson<BasicTextObject>(tj)?.GetText() ?? "" : "";
        var wav = message.Ports.TryGetValue(_speechPort, out var sj) && !string.IsNullOrWhiteSpace(sj)
            ? MpaiJson.FromJson<BasicSpeechObject>(sj)?.Data ?? [] : [];
        var (duration, envelope) = Speech.Envelope(wav, EnvelopeSeconds);

        var motion = BodyMotion.Generate(text, duration, envelope, emotion, degree);
        MpaiDiag.Emotion("GBD", $"{emotion ?? "no emotion"} {degree:0.00}: {duration:0.00} s, {motion.Beats} beats, {motion.Questions} question(s), {motion.Nods} nod(s)");
        var bdo = MpaiJson.ToJson(BodyDescriptorsObject.FromContent(motion.Bvh, "BVH"));
        return Task.FromResult(new Message
        {
            MessageId   = message.MessageId,
            MessageType = "BodyDescriptorsObject",
            DataType    = "PAF-BDO-V1.6",
            Payload     = bdo,
            Ports       = new Dictionary<string, string> { [_outPort] = bdo }
        });
    }
}

// The duration and loudness envelope (0..1) of a 16-bit PCM WAV, as GFD reads them.
public static class Speech
{
    public static (double Duration, double[] Envelope) Envelope(byte[] wav, double window)
    {
        if (wav.Length < 44) return (0, []);
        int channels = BitConverter.ToInt16(wav, 22), rate = BitConverter.ToInt32(wav, 24), bits = BitConverter.ToInt16(wav, 34);
        if (channels <= 0 || rate <= 0 || bits != 16) return (0, []);
        int pos = 12, offset = 44, length = wav.Length - 44;
        while (pos + 8 <= wav.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
            var len = BitConverter.ToInt32(wav, pos + 4);
            if (id == "data") { offset = pos + 8; length = Math.Min(len, wav.Length - offset); break; }
            pos += 8 + len + (len & 1);
        }
        int stride = 2 * channels, samples = length / stride;
        double duration = (double)samples / rate;
        int windows = Math.Max(1, (int)Math.Round(duration / window)), per = Math.Max(1, samples / windows);
        var env = new double[windows];
        for (var w = 0; w < windows; w++)
        {
            double sum = 0; var n = 0;
            for (var s = w * per; s < (w + 1) * per && s < samples; s++) { var v = (double)BitConverter.ToInt16(wav, offset + s * stride); sum += v * v; n++; }
            env[w] = n > 0 ? Math.Sqrt(sum / n) / 32768.0 : 0;
        }
        var max = env.DefaultIfEmpty(0).Max();
        if (max > 1e-6) for (var i = 0; i < env.Length; i++) env[i] = Math.Min(1, env[i] / max * 1.4);
        return (duration, env);
    }
}
