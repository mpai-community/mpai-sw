using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;
using Mpai.Core;

namespace Mpai.Aims.Asr;

// MMC-ASR-V2.5 â€” self-contained IAimProcessor.
// Reads its own port names from 1MMC-ASR-V2.5-I01.json at startup.
public sealed class AsrAimProcessor : IAimProcessor
{
    // TWO PATHS THROUGH ONE RECOGNISER. Speech at Port 1 is a question about an
    // image and its text goes on to whatever consumes it; speech at Port 2 is a
    // reply to something the User Agent asked, and its text is for the User Agent
    // to read. The Port the speech arrives on says which it is, so nothing has to
    // infer it from what happens to be missing.
    private readonly string        _imageIn;
    private readonly string        _replyIn;
    private readonly string        _imageOut;
    private readonly string        _replyOut;
    private readonly WhisperAsrAim _asr;

    public string InstanceId { get; }

    public AsrAimProcessor(
        string        instanceId,
        WhisperAsrAim asr,
        AimPortReader      ports)
    {
        InstanceId   = instanceId;
        _asr         = asr;
        _imageIn  = ports.Input("OSD-BSO-V1.5", 1);
        _replyIn  = ports.Input("OSD-BSO-V1.5", 2);
        _imageOut = ports.Output("OSD-BTO-V1.5", 1);
        _replyOut = ports.Output("OSD-BTO-V1.5", 2);
    }

    public async Task<Message> ProcessAsync(Message message)
    {
        // Whichever Port carried it decides where the text goes.
        System.Console.WriteLine($"[ASR] ports in: {string.Join(", ", message.Ports.Keys)}; looking for reply={_replyIn} image={_imageIn}");
        var reply  = message.Ports.ContainsKey(_replyIn);
        var source = reply ? _replyIn : _imageIn;
        if (!message.Ports.TryGetValue(source, out var speechJson)) return null!;
        var speech = MpaiJson.FromJson<BasicSpeechObject>(speechJson);
        try { int __r7=(speech?.SpeechQualifier?.Format?.ContentFormats?.RawData?.SamplingFrequency is double __rf && __rf>0)?(int)__rf:16000; Mpai.Core.MpaiDiag.DumpPcm(speech?.Data, __r7, "7_ASR_in"); } catch {}
        try { if (speech?.Data?.Length > 0) Mpai.Core.MpaiDiag.WriteBytes("C_asr_in.wav", speech.Data); Mpai.Core.MpaiDiag.Append("trace.log", "ASR-IN bytes=" + (speech?.Data?.Length ?? -1) + System.Environment.NewLine); } catch {}
        try { Mpai.Core.MpaiDiag.Append("hci-diag.log", System.DateTime.Now.ToString("HH:mm:ss.fff") + "  [ASR-IN] bytes=" + (speech?.Data?.Length ?? -1) + System.Environment.NewLine); if (speech?.Data?.Length > 0) Mpai.Core.MpaiDiag.WriteBytes("asr-input.wav", speech.Data); } catch {}

        Trace("[ASR-IN] speechBytes=" + (speech?.Data?.Length ?? -1));

        try { /*AMQTRACE_ASR_IN*/ if(speech?.Data?.Length>0) Mpai.Core.MpaiDiag.WriteBytes("3_ASR_in.wav", speech.Data); } catch {}
        var text = await _asr.ProcessAsync(speech);

        Trace("[ASR-OUT] text=<" + (text?.GetText() ?? "<null>") + ">");

        var json = MpaiJson.ToJson(text);
        try { /*AMQTRACE_ASR_OUT*/ Mpai.Core.MpaiDiag.WriteText("4_ASR_out.txt", text?.GetText() ?? "(null)"); } catch {}

        return new Message
        {
            MessageId   = message.MessageId,
            MessageType = "BasicTextObject",
            DataType    = text.Header,
            Payload     = json,
            Ports       = new Dictionary<string, string> { [reply ? _replyOut : _imageOut] = json }
        };
    }

    private static void Trace(string m)
    {
        Mpai.Core.MpaiDiag.Append("asr-trace.log",
            System.DateTime.Now.ToString("HH:mm:ss.fff") + "  " + m + "\n");
    }
}
