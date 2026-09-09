using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;
using Mpai.Core;

namespace Mpai.Aims.Audio;

// CAE-AOD-V1.0 — self-contained IAimProcessor.
// Reads its own port names from 1CAE-AOD-V1.0-I01.json at startup.
//
// AOD delivers AUDIO (OSD-AUO-V1.5). It now reads a Basic Audio Object
// directly (speech delivery is SOD's job), so there is no speech->audio
// coercion here — AOD is a pure audio transducer.
public sealed class AodAimProcessor : IAimProcessor
{
    private readonly string            _inputPort;
    private readonly string            _outputPort;
    private readonly IAudioDeliveryAim _aod;

    public string InstanceId { get; }

    public AodAimProcessor(
        string            instanceId,
        IAudioDeliveryAim aod,
        AimPortReader          ports)
    {
        InstanceId  = instanceId;
        _aod        = aod;
        _inputPort  = ports.Input("OSD-AUO-V1.5");
        _outputPort = ports.Output("OSD-AUO-V1.5");
    }

    public async Task<Message> ProcessAsync(Message message)
    {
        var audio = MpaiJson.FromJson<BasicAudioObject>(message.Ports[_inputPort]);
        await _aod.DeliverAsync(audio);

        return new Message
        {
            MessageId   = message.MessageId,
            MessageType = "BasicAudioObject",
            DataType    = audio.Header,
            Payload     = message.Ports[_inputPort],
            Ports       = new Dictionary<string, string>
            {
                [_outputPort] = message.Ports[_inputPort]
            }
        };
    }
}
