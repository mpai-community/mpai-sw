using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Osd.VisualScene;   // ScrfdFaceDetector

namespace Mpai.Cve.Vsi;

// CVE-VSI-V1.0 - Visual Scene object Identification (the scanner).
//
// Reads the Basic Visual Scene Descriptors and, for each visual object, decides
// its type and TAGS the object's Visual Qualifier with VisualObjectType (Face |
// Object). It runs the SCRFD face detector on the object's image data: a face
// found -> "Face", otherwise "Object". It then emits every (re-qualified) Basic
// Visual Object on a single OSD-BVO output. Both downstream identifiers receive
// the stream (routing is by data type); each acts only on its own type - Face
// Recognition on the faces, Visual Instance Identification on the rest. The type
// travels in the data (the qualifier), not in the port.
public sealed class CveVsiAimProcessor : IAimProcessor
{
    private readonly string _instanceId;
    private readonly string _inPort;    // OSD-BVS
    private readonly string _outPort;   // OSD-BVO
    private readonly ScrfdFaceDetector _scrfd;

    public CveVsiAimProcessor(string instanceId, ScrfdFaceDetector scrfd, AimPortReader ports)
    {
        _instanceId = instanceId;
        _scrfd      = scrfd;
        _inPort     = ports.Input("OSD-BVS-V1.5");
        _outPort    = ports.Output("OSD-BVO-V1.5");
    }

    public string InstanceId => _instanceId;

    public Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(_inPort, out var json) || string.IsNullOrWhiteSpace(json))
            return Task.FromResult(Message.Error(message.MessageId, _instanceId, "no Basic Visual Scene Descriptors on input port"));

        var scene = MpaiJson.FromJson<BasicVisualSceneDescriptors>(json);
        if (scene is null)
            return Task.FromResult(Message.Error(message.MessageId, _instanceId, "could not parse Basic Visual Scene Descriptors"));

        // A scene has one or more visual objects. This implementation emits the
        // first object (the current single-object scene); a fuller scanner would
        // emit each object in turn. Classify by SCRFD, tag the qualifier, emit.
        var ports = new Dictionary<string, string>();
        foreach (var entry in scene.BasicVisualSceneDescriptorsEntries)
        {
            var obj = entry.VObjectIDOrVObject;
            if (obj is null) continue;

            string type = "Object";
            if (obj.Data is { Length: > 0 })
            {
                try { type = _scrfd.Detect(obj.Data).Count > 0 ? "Face" : "Object"; }
                catch { type = "Object"; }
            }

            var tagged = ReQualify(obj, type);
            ports[_outPort] = MpaiJson.ToJson(tagged);
            break;   // single-object scene: emit the first
        }

        if (ports.Count == 0)
            return Task.FromResult(Message.Error(message.MessageId, _instanceId, "no visual object in scene"));

        return Task.FromResult(new Message
        {
            MessageId   = message.MessageId,
            MessageType = message.MessageType,
            Ports       = ports
        });
    }

    // Return the object with its Visual Qualifier's VisualObjectType set.
    private static BasicVisualObject ReQualify(BasicVisualObject obj, string visualObjectType)
    {
        var q  = obj.VisualQualifier;
        var at = q?.Attributes;
        var newAttrs = new VisualAttributes
        {
            Source               = at?.Source,
            VisualObjectType     = visualObjectType,
            Metadata             = at?.Metadata,
            ObjectID             = at?.ObjectID,
            EntityInternalStatus = at?.EntityInternalStatus,
            Device               = at?.Device
        };
        var newQual = new VisualQualifier
        {
            Header              = q?.Header ?? "TFA-VIQ-V1.5",
            MInstanceID         = q?.MInstanceID,
            UEnvironmentID      = q?.UEnvironmentID,
            VisualQualifierID   = q?.VisualQualifierID ?? "",
            VisualQualifierTime = q?.VisualQualifierTime,
            SubType             = q?.SubType,
            Format              = q?.Format,
            Attributes          = newAttrs,
            DataXMData          = q?.DataXMData,
            DescrMetadata       = q?.DescrMetadata
        };
        return new BasicVisualObject
        {
            Header              = obj.Header,
            BasicVisualObjectID = obj.BasicVisualObjectID,
            FileName            = obj.FileName,
            Data                = obj.Data,
            VisualQualifier     = newQual
        };
    }
}
