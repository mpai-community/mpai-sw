using System;
using System.Threading.Tasks;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Osd.Tod;

// A headless 3D delivery device: reports the 3D scene it would render (the model
// plus which animation streams drive it) rather than driving a display. It proves
// the 3OD delivery path through the Controller without a graphical device. The
// graphical device (a WebView-backed 3D renderer) is provided by the host
// application that owns a display surface.
public sealed class ConsoleModelDelivery : I3DModelDeliveryAim
{
    public Task DeliverAsync(
        Basic3DModelObject model,
        FaceDescriptorsObject? faceAnimation = null,
        BodyDescriptorsObject? bodyAnimation = null)
    {
        var anim = "";
        if (faceAnimation is not null) anim += " + face animation";
        if (bodyAnimation is not null) anim += " + body animation";
        AimLog.Write("OSD-3OD-V1.5",
            $"rendering 3D scene: model {model.Basic3DModelObjectID} ({model.Data.Length:N0} bytes){anim}");
        return Task.CompletedTask;
    }
}
