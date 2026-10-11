using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Osd.Bls;

// OSD-BLS-V1.5 - Basic LiDAR Scene Description, as an AIF IAimProcessor.
//
// Front-end describer of MMC-HCI. A structural transform: it takes the acquired
// Basic LiDAR Object (OSD-BLO) - which carries its own 3D SpaceTime - and packages
// it into a Basic LiDAR Scene Descriptors (OSD-BLS): a one-object scene whose entry
// carries the object at its spatial attitude. In MMC-HCI the LiDAR scene is fused
// by Audio-Visual Alignment to give the visual objects their (depth-resolved)
// points of view.
public sealed class BlsAimProcessor : IAimProcessor
{
    private readonly string _instanceId;
    private readonly string _inPort;    // OSD-BLO
    private readonly string _outPort;   // OSD-BLS

    public BlsAimProcessor(string instanceId, AimPortReader ports)
    {
        _instanceId = instanceId;
        _inPort     = ports.Input("OSD-BLO-V1.5");
        _outPort    = ports.Output("OSD-BLS-V1.5");
    }

    public string InstanceId => _instanceId;

    public Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(_inPort, out var json) || string.IsNullOrWhiteSpace(json))
            return Task.FromResult(Message.Error(message.MessageId, _instanceId, "no Basic LiDAR Object on input port"));

        var obj = MpaiJson.FromJson<BasicLiDARObject>(json);
        if (obj is null)
            return Task.FromResult(Message.Error(message.MessageId, _instanceId, "could not parse Basic LiDAR Object"));

        var scene = new BasicLiDARSceneDescriptors
        {
            Header                          = "OSD-BLS-V1.5",
            BasicLiDARSceneDescriptorsID    = System.Guid.NewGuid().ToString(),
            BasicLiDARSceneDescriptorsSpaceTime = obj.BasicLiDARObjectSpaceTime,
            ObjectCount                     = 1,
            BasicLiDARSceneDescriptorsEntries = new List<BasicLiDARSceneEntry>
            {
                new BasicLiDARSceneEntry
                {
                    ObjectSpaceTime  = obj.BasicLiDARObjectSpaceTime,
                    ObjectIDOrObject = new List<object> { obj }
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

// ---- minimal LiDAR DTOs (OSD-BLO / OSD-BLS), as Mpai.Core.OSD has none yet ----
// Mirrors schemas OSD/V1.5/data/BasicLiDARObject.json and BasicLiDARSceneDescriptors.json.
