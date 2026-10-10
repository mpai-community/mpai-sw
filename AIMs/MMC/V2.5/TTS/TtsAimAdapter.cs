using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;

using Mpai.Core;

namespace Mpai.Aims.Tts;

// AIF adapter for Text to Speech (MMC-TTS-V2.5).
//
// NOTE: Transitional 鈥?see TiqAimAdapter for the rationale.
// Ports keyed by Data Type and number (PortKey), as in 1MMC-TTS-V2.5-I01.json
// - never by name.
public sealed class TtsAimAdapter
    : IAimProcessor
{
    public static readonly string InputPort  = PortKey.Of("OSD-BTO-V1.5", 1);
    public static readonly string OutputPort = PortKey.Of("OSD-BSO-V1.5", 1);

    private readonly ITtsAim tts;

    public string InstanceId { get; }

    public TtsAimAdapter(
        string instanceId,
        ITtsAim tts)
    {
        InstanceId = instanceId;
        this.tts = tts;
    }

    public async Task<Message> ProcessAsync(
        Message message)
    {
        var text =
            MpaiJson.FromJson<BasicTextObject>(
                message.Ports[InputPort]);

        var speech =
            await tts.ProcessAsync(text);

        var json = MpaiJson.ToJson(speech);

        return new Message
        {
            MessageId   = message.MessageId,
            MessageType = "BasicSpeechObject",
            DataType    = speech.Header,
            Payload     = json,
            Ports       = new Dictionary<string, string> { [OutputPort] = json }
        };
    }
}
