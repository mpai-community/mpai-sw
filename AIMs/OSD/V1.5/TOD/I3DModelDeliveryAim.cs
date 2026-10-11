using System.Threading.Tasks;
using Mpai.Core.OSD;

namespace Mpai.Osd.Tod;

// 3D Model Object Delivery device abstraction. Delivers a 3D scene to a 3D
// renderer: the 3D Model (static) together with the animation streams that drive
// it - a Face animation (Face Descriptors) and a Body animation (Body Descriptors),
// each optional and independent. The renderer combines model + animation and
// renders (3D to 2D); posing and rendering are one act. Further animation streams
// can be added as additional parameters/overloads without disturbing the model
// delivery. Independent of the other Object Delivery AIMs.
public interface I3DModelDeliveryAim
{
    Task DeliverAsync(
        Basic3DModelObject model,
        FaceDescriptorsObject? faceAnimation = null,
        BodyDescriptorsObject? bodyAnimation = null);
}
