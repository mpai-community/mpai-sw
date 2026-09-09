using System.Collections.Generic;
using System.Threading.Tasks;
using AIF.Controller;
using Mpai.Core;
namespace Mpai.Aims.Tts;
// MMC-TTS-V2.5 - self-contained IAimProcessor.
// Reads its own port names from 1MMC-TTS-V2.5-I01.json at startup.
//
// TTS reads a Text Object (its input port accepts both OSD-BTO and OSD-TXO) and
// produces SPEECH: its output is a Basic Speech Object carrying a SpeechQualifier,
// on a port typed for both OSD-BSO and OSD-SPO so the object's speech metadata is
// preserved to whatever consumes it (SOD, ASR, or the RSR renderer).
public sealed class TtsAimProcessor : IAimProcessor
{
    private readonly string      _inputPort;
    private readonly string      _outputPort;
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
    }
    public async Task<Message> ProcessAsync(Message message)
    {
        var text   = MpaiJson.FromJson<BasicTextObject>(message.Ports[_inputPort]);
        var speech = await _tts.ProcessAsync(text);
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
}
