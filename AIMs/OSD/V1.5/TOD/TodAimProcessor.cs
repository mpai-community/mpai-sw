using System.Collections.Generic;
using System.Threading.Tasks;
using AIF.Controller;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Osd.Tod;

// OSD-3OD-V1.5 - 3D Model Object Delivery. Self-contained IAimProcessor.
// Reads its own port names from 1OSD-3OD-V1.5-I01.json at startup.
//
// 3OD delivers a 3D scene to a device (a 3D renderer): the 3D Model (its
// ModelObject port accepts both OSD-B3O and OSD-3DO) together with the animation
// streams that drive it - Face Descriptors (FaceAnimation port) and Body
// Descriptors (BodyAnimation port), each optional and on its own port so further
// animation streams can be added independently. The renderer combines model +
// animation and renders (3D to 2D); posing and rendering are one act. The objects
// stay typed to the device edge. Independent of the other Object Delivery AIMs.
public sealed class TodAimProcessor : IAimProcessor
{
    private readonly string                _modelPort;
    private readonly string                _faceAnimPort;
    private readonly string                _bodyAnimPort;
    private readonly string                _outputPort;
    private readonly I3DModelDeliveryAim   _tod;
    public string InstanceId { get; }

    public TodAimProcessor(
        string              instanceId,
        I3DModelDeliveryAim tod,
        AimPortReader       ports)
    {
        InstanceId    = instanceId;
        _tod          = tod;
        _modelPort    = ports.Input("OSD-B3O-V1.5");   // ModelObject, dual-typed [OSD-B3O, OSD-3DO]
        _faceAnimPort = ports.Input("PAF-FDO-V1.6");   // FaceAnimation
        _bodyAnimPort = ports.Input("PAF-BDO-V1.6");   // BodyAnimation
        _outputPort   = ports.Output("OSD-B3O-V1.5");  // OutputVisual, dual-typed [OSD-B3O, OSD-3DO]
    }

    public async Task<Message> ProcessAsync(Message message)
    {
        var model = ReadModel(message);
        if (model is null || model.Data.Length == 0)
        {
            System.Console.WriteLine("[OSD-3OD-V1.5] nothing to render - no 3D Model Object.");
            return Passthrough(message);
        }

        var faceAnim = Read<FaceDescriptorsObject>(message, _faceAnimPort);
        var bodyAnim = Read<BodyDescriptorsObject>(message, _bodyAnimPort);

        await _tod.DeliverAsync(model, faceAnim, bodyAnim);   // deliver model + animation to the renderer

        return Passthrough(message);
    }

    private Basic3DModelObject? ReadModel(Message message)
    {
        if (!message.Ports.TryGetValue(_modelPort, out var json) || string.IsNullOrWhiteSpace(json))
            return null;
        return MpaiJson.FromJson<Basic3DModelObject>(json);
    }

    private static T? Read<T>(Message message, string port) where T : class
    {
        if (!message.Ports.TryGetValue(port, out var json) || string.IsNullOrWhiteSpace(json))
            return null;
        return MpaiJson.FromJson<T>(json);
    }

    private Message Passthrough(Message message) => new()
    {
        MessageId   = message.MessageId,
        MessageType = "Basic3DModelObject",
        DataType    = "OSD-B3O-V1.5",
        Payload     = message.Ports.TryGetValue(_modelPort, out var m) ? m : "",
        Ports       = message.Ports.TryGetValue(_modelPort, out var mm)
            ? new Dictionary<string, string> { [_outputPort] = mm }
            : new Dictionary<string, string>()
    };
}
