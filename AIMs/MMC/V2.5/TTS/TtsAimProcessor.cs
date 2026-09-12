using System.Collections.Generic;
using System.Threading.Tasks;
using AIF.Controller;
using Mpai.Core;
using Mpai.Core.OSD;
namespace Mpai.Aims.Tts;
// MMC-TTS-V2.5 - self-contained IAimProcessor.
// Reads its own port names from 1MMC-TTS-V2.5-I01.json at startup.
//
// When a Speech Personal Status (MMC-SPS) is supplied, its emotion shapes the
// Piper prosody (speaking rate + variation) so the voice carries the feeling.
// The port is optional: absent => neutral synthesis (MAD/MAT/MAC unchanged).
public sealed class TtsAimProcessor : IAimProcessor
{
    private readonly string      _inputPort;
    private readonly string      _outputPort;
    private readonly string      _spsPort;
    private readonly PiperTtsAim _tts;
    public string InstanceId { get; }
    public TtsAimProcessor(
        string      instanceId,
        PiperTtsAim tts,
        AimPortReader    ports)
    {
        InstanceId  = instanceId;
        _tts        = tts;
        _inputPort  = ports.Input("OSD-BTO-V1.5");   // dual-typed port [OSD-BTO, OSD-TXO]
        _outputPort = ports.Output("OSD-BSO-V1.5");  // dual-typed port [OSD-BSO, OSD-SPO]
        _spsPort    = ports.InputOrDefault("MMC-SPS-V2.5", 1, string.Empty);   // optional emotion
    }
    public async Task<Message> ProcessAsync(Message message)
    {
        var text = MpaiJson.FromJson<BasicTextObject>(message.Ports[_inputPort]);

        SpeechPersonalStatus? sps = null;
        if (!string.IsNullOrEmpty(_spsPort) &&
            message.Ports.TryGetValue(_spsPort, out var spsJson) &&
            !string.IsNullOrWhiteSpace(spsJson))
        {
            sps = MpaiJson.FromJson<SpeechPersonalStatus>(spsJson);
        }

        var speech = await _tts.ProcessAsync(text, ProsodyArgs(sps));
        var json   = MpaiJson.ToJson(speech);
        return new Message
        {
            MessageId   = message.MessageId,
            MessageType = "BasicSpeechObject",
            DataType    = speech.Header,
            Payload     = json,
            Ports       = new Dictionary<string, string> { [_outputPort] = json }
        };
    }

    // Map the Speech Personal Status emotion to Piper prosody flags (speaking rate
    // + variation), blended toward Piper's defaults by the emotion Degree. Absent
    // or neutral => no flags, so neutral synthesis is unchanged.
    private static string ProsodyArgs(SpeechPersonalStatus? sps)
    {
        var em  = sps?.SpeechEmotion;
        var cat = em?.Category?.ToUpperInvariant();
        if (string.IsNullOrEmpty(cat) || cat == "CALMNESS" || cat == "NEUTRAL")
            return "";

        double d = em!.Degree ?? 0.7; d = 0.7 + 0.3 * d;
        if (d < 0.0) d = 0.0;
        if (d > 1.0) d = 1.0;

        (double len, double noise, double noisew) t = cat switch
        {
            "HAPPINESS" => (0.80, 0.95, 1.00),
            "ANGER"     => (0.78, 1.00, 0.90),
            "FEAR"      => (0.82, 0.95, 0.95),
            "SADNESS"   => (1.35, 0.35, 0.50),
            _           => (1.0, 0.667, 0.8)
        };

        double L = 1.0   + (t.len    - 1.0)   * d;
        double N = 0.667 + (t.noise  - 0.667) * d;
        double W = 0.8   + (t.noisew - 0.8)   * d;

        var ci = System.Globalization.CultureInfo.InvariantCulture;
        return "--length_scale " + L.ToString("0.###", ci) +
               " --noise_scale " + N.ToString("0.###", ci) +
               " --noise_w "     + W.ToString("0.###", ci);
    }
}
