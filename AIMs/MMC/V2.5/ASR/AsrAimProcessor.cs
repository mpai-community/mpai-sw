using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;
using Mpai.Core;

namespace Mpai.Aims.Asr;

// MMC-ASR-V2.5 â€” self-contained IAimProcessor.
// Reads its own port names from 1MMC-ASR-V2.5-I01.json at startup.
public sealed class AsrAimProcessor : IAimProcessor
{
    private readonly string        _inputPort;
    private readonly string        _outputPort;
    private readonly WhisperAsrAim _asr;

    public string InstanceId { get; }

    public AsrAimProcessor(
        string        instanceId,
        WhisperAsrAim asr,
        AimPortReader      ports)
    {
        InstanceId   = instanceId;
        _asr         = asr;
        _inputPort   = ports.Input("OSD-BSO-V1.5");
        _outputPort  = ports.Output("OSD-BTO-V1.5");
    }

    public async Task<Message> ProcessAsync(Message message)
    {
        var speech = MpaiJson.FromJson<BasicSpeechObject>(message.Ports[_inputPort]);

        Trace("[ASR-IN] speechBytes=" + (speech?.Data?.Length ?? -1));

        var text = await _asr.ProcessAsync(speech);

        Trace("[ASR-OUT] text=<" + (text?.GetText() ?? "<null>") + ">");

        var json = MpaiJson.ToJson(text);

        return new Message
        {
            MessageId   = message.MessageId,
            MessageType = "BasicTextObject",
            DataType    = text.Header,
            Payload     = json,
            Ports       = new Dictionary<string, string> { [_outputPort] = json }
        };
    }

    private static void Trace(string m)
    {
        try
        {
            System.IO.File.AppendAllText(
                @"D:\AI\asr-trace.log",
                System.DateTime.Now.ToString("HH:mm:ss.fff") + "  " + m + "\n");
        }
        catch { }
    }
}
