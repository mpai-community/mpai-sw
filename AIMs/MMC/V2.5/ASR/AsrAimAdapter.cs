using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;

using Mpai.Core;

namespace Mpai.Aims.Asr;

// AIF adapter for Automatic Speech Recognition (MMC-ASR-V2.5).
//
// NOTE: Transitional 鈥?see TiqAimAdapter for the rationale.
// Port names must match 1MMC-ASR-V2.5-I01.json ExternalPorts:
// "InputAudio" (Input), "OutputText" (Output).
public sealed class AsrAimAdapter
    : IAimProcessor
{
    public const string InputPort  = "InputAudio";
    public const string OutputPort = "OutputText";

    private readonly IAsrAim asr;

    public string InstanceId { get; }

    public AsrAimAdapter(
        string instanceId,
        IAsrAim asr)
    {
        InstanceId = instanceId;
        this.asr = asr;
    }

    public async Task<Message> ProcessAsync(
        Message message)
    {
        var audio =
            MpaiJson.FromJson<BasicAudioObject>(
                message.Ports[InputPort]);

        var text =
            await asr.ProcessAsync(audio.AsSpeech());

        var json = MpaiJson.ToJson(text);

        return new Message
        {
            MessageId   = message.MessageId,
            MessageType = "BasicTextObject",
            DataType    = text.Header,
            Payload     = json,
            Ports       = new Dictionary<string, string> { [OutputPort] = json }
        };
    }
}
