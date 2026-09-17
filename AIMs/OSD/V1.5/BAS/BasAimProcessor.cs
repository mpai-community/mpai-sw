using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Osd.Bas;

// OSD-BAS-V1.5 - Basic Audio Scene Description, as an AIF IAimProcessor.
//
// Front-end describer of MMC-HCI. A structural transform: it takes the acquired
// Basic Audio Object (OSD-BAO) and packages it into a Basic Audio Scene
// Descriptors (OSD-BAS) - a one-object scene whose entry carries the object and
// its point of view. The direction of the sound (its point of view) comes from
// the microphone-array acquisition; this AIM describes the scene from the object
// it is given.
public sealed class BasAimProcessor : IAimProcessor
{
    private readonly string _instanceId;
    private readonly string _inPort;    // OSD-BAO
    private readonly string _outPort;   // OSD-BAS

    public BasAimProcessor(string instanceId, AimPortReader ports)
    {
        _instanceId = instanceId;
        _inPort     = ports.Input("OSD-BAO-V1.5");
        _outPort    = ports.Output("OSD-BAS-V1.5");
    }

    public string InstanceId => _instanceId;

    public Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(_inPort, out var json) || string.IsNullOrWhiteSpace(json))
            return Task.FromResult(Message.Error(message.MessageId, _instanceId, "no Basic Audio Object on input port"));

        var obj = MpaiJson.FromJson<BasicAudioObject>(json);
        if (obj is null)
            return Task.FromResult(Message.Error(message.MessageId, _instanceId, "could not parse Basic Audio Object"));

        var scene = new BasicAudioSceneDescriptors
        {
            Header                         = "OSD-BAS-V1.5",
            BasicAudioSceneDescriptorsID   = System.Guid.NewGuid().ToString(),
            AudioObjectCount               = 1,
            BasicAudioSceneDescriptorsEntries = new List<BasicAudioSceneEntry>
            {
                new BasicAudioSceneEntry
                {
                    AudioObjectIDOrAudioObject = obj,
                    PointOfView                = new PointOfView()   // from mic-array acquisition
                }
            }
        };

        return Task.FromResult(new Message
        {
            MessageId   = message.MessageId,
            MessageType = message.MessageType,
            Ports       = new Dictionary<string, string> { [_outPort] = MpaiJson.ToJson(scene) }
        });
    }
}
