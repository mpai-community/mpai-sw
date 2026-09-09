using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;

using Mpai.Core;

namespace Mpai.Aims.Tts;

// AIF adapter for Text to Speech (MMC-TTS-V2.5).
//
// NOTE: Transitional 鈥?see TiqAimAdapter for the rationale.
// Port names must match 1MMC-TTS-V2.5-I01.json ExternalPorts
// (to be confirmed once that file is finalised).
public sealed class TtsAimAdapter
    : IAimProcessor
{
    public const string InputPort  = "InputText";
    public const string OutputPort = "OutputAudio";

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
