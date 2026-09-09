using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;

using Mpai.Core;

namespace Mpai.Aims.Audio;

// AIF adapter for Audio Object Delivery (CAE-AOD-V1.0).
//
// NOTE: Transitional 鈥?see TiqAimAdapter for the rationale.
// Port names must match 1CAE-AOD-V1.0-I01.json ExternalPorts.
public sealed class AodAimAdapter
    : IAimProcessor
{
    public const string InputPort  = "InputAudio";
    public const string OutputPort = "OutputAudio";

    private readonly IAudioDeliveryAim aod;

    public string InstanceId { get; }

    public AodAimAdapter(
        string instanceId,
        IAudioDeliveryAim aod)
    {
        InstanceId = instanceId;
        this.aod = aod;
    }

    public async Task<Message> ProcessAsync(
        Message message)
    {
        var speech =
            MpaiJson.FromJson<BasicSpeechObject>(
                message.Ports[InputPort]);

        await aod.DeliverAsync(speech.AsAudio());

        return new Message
        {
            MessageId   = message.MessageId,
            MessageType = "BasicAudioObject",
            DataType    = speech.Header,
            Payload     = message.Ports[InputPort],
            Ports       = new Dictionary<string, string>
            {
                [OutputPort] = message.Ports[InputPort]
            }
        };
    }
}
