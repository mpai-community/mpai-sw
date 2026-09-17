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
        try { int __r7=(speech?.SpeechQualifier?.Format?.ContentFormats?.RawData?.SamplingFrequency is double __rf && __rf>0)?(int)__rf:16000; Mpai.Core.MpaiDiag.DumpPcm(speech?.Data, __r7, "7_ASR_in"); } catch {}
        try { System.IO.Directory.CreateDirectory(@"C:\Users\Leonardo\Downloads\hci-trace"); if (speech?.Data?.Length > 0) System.IO.File.WriteAllBytes(@"C:\Users\Leonardo\Downloads\hci-trace\C_asr_in.wav", speech.Data); System.IO.File.AppendAllText(@"C:\Users\Leonardo\Downloads\hci-trace\trace.log", "ASR-IN bytes=" + (speech?.Data?.Length ?? -1) + System.Environment.NewLine); } catch {}
        try { System.IO.File.AppendAllText(@"C:\Users\Leonardo\Downloads\hci-diag.log", System.DateTime.Now.ToString("HH:mm:ss.fff") + "  [ASR-IN] bytes=" + (speech?.Data?.Length ?? -1) + System.Environment.NewLine); if (speech?.Data?.Length > 0) System.IO.File.WriteAllBytes(@"C:\Users\Leonardo\Downloads\asr-input.wav", speech.Data); } catch {}

        Trace("[ASR-IN] speechBytes=" + (speech?.Data?.Length ?? -1));

        try { /*AMQTRACE_ASR_IN*/ System.IO.Directory.CreateDirectory(@"C:\Users\Leonardo\Downloads\amq-trace"); if(speech?.Data?.Length>0) System.IO.File.WriteAllBytes(@"C:\Users\Leonardo\Downloads\amq-trace\3_ASR_in.wav", speech.Data); } catch {}
        var text = await _asr.ProcessAsync(speech);

        Trace("[ASR-OUT] text=<" + (text?.GetText() ?? "<null>") + ">");

        var json = MpaiJson.ToJson(text);
        try { /*AMQTRACE_ASR_OUT*/ System.IO.File.WriteAllText(@"C:\Users\Leonardo\Downloads\amq-trace\4_ASR_out.txt", text?.GetText() ?? "(null)"); } catch {}

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
