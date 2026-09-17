using System;
using System.Linq;

using AIF.Controller;

using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Osd.VisualScene;   // YoloxObjectDetector, ObjectDetection

namespace Mpai.Osd.Vii;

// OSD-VII-V1.5 - Visual Instance Identification, as an AIF IAimProcessor.
//
// Receives a Basic Visual Object (the Target Visual Object to be identified) and
// produces its Visual Instance Identifier (OSD-IID): the identity of the object
// against a taxonomy. It is the general-object analogue of PAF-FIR - where FIR
// identifies a located FACE object, VII identifies a located visual object of any
// COCO class. The scene has already LOCATED the object (BVS locates the BVOs in
// OSD-BMS = BAS + BSS + BVS); VII's narrow job is to say WHAT that located object
// is.
//
// Engine: YOLOX (yolox_s.onnx). The BVO carries one object's image; YOLOX runs on
// it and the highest-confidence detection becomes the instance identity. The IID's
// InstanceLabel is the COCO class (e.g. "zebra"), LabelConfidenceLevel the detector
// score, and the taxonomy path is the flat ["visual","object",<class>] (a pragmatic
// placeholder until a formal MPAI visual taxonomy is available - a real taxonomy
// URI would go in Taxonomy.TaxonomyDataURI).
//
// "Empty identification is not representable" (OSD-IID rule): if no object is
// detected, VII returns a coarse candidate ["visual","object"] labelled "object"
// with the top score (or zero), rather than an empty identifier.
public sealed class ViiAimProcessor : IAimProcessor
{
    private const string IidHeader = "OSD-IID-V1.5";

    private readonly string _instanceId;
    private readonly YoloxObjectDetector _detector;
    private readonly string _inPort;
    private readonly string _outPort;

    public ViiAimProcessor(
        string instanceId,
        YoloxObjectDetector detector,
        AimPortReader ports)
    {
        _instanceId = instanceId;
        _detector   = detector;
        _inPort     = ports.Input("OSD-BVO-V1.5");
        _outPort    = ports.Output("OSD-IID-V1.5");
    }

    public string InstanceId => _instanceId;

    public System.Threading.Tasks.Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(_inPort, out var bvoJson))
            return System.Threading.Tasks.Task.FromResult(
                Message.Error(message.MessageId, _instanceId, "no Target Visual Object on input port"));

        var visual = MpaiJson.FromJson<BasicVisualObject>(bvoJson);
        if (visual is null || visual.Data.Length == 0)
            return System.Threading.Tasks.Task.FromResult(
                Message.Error(message.MessageId, _instanceId, "empty Target Visual Object"));

        // Type guard: VII identifies NON-FACE visual objects. Faces are FIR's; a
        // face-tagged object is declined here so EXACTLY ONE Instance Identifier is
        // produced per object. Untagged objects default to VII (generic).
        var visualObjectType = visual.VisualQualifier?.Attributes?.VisualObjectType;
        if (visualObjectType == "Face")
            return System.Threading.Tasks.Task.FromResult(new Message
            {
                MessageId   = message.MessageId,
                MessageType = message.MessageType,
                Ports       = new System.Collections.Generic.Dictionary<string, string>()
            });

        // Identify the object: run YOLOX, take the highest-confidence detection.
        var detections = _detector.Detect(visual.Data);
        var best = detections.OrderByDescending(d => d.Score).FirstOrDefault();

        InstanceIdentifier iid = best is not null
            ? Identify(best.ClassName, best.Score)
            : Coarse();

        return System.Threading.Tasks.Task.FromResult(new Message
        {
            MessageId = message.MessageId,
            MessageType = message.MessageType,
            Ports = new System.Collections.Generic.Dictionary<string, string>
            {
                [_outPort] = MpaiJson.ToJson(iid)
            }
        });
    }

    // A confident identification: label = COCO class, taxonomy = [visual, object, class].
    private static InstanceIdentifier Identify(string className, float score) => new()
    {
        Header = IidHeader,
        InstanceIdentifier_ = className,
        InstanceIdentifierData =
        {
            new InstanceCandidate
            {
                InstanceLabel = className,
                LabelConfidenceLevel = score,
                Taxonomy = new InstanceTaxonomy
                {
                    TaxonomyLevelIDs = { "visual", "object", className }
                }
            }
        }
    };

    // No object detected: a coarse candidate, so the identifier is never empty.
    private static InstanceIdentifier Coarse() => new()
    {
        Header = IidHeader,
        InstanceIdentifier_ = "object",
        InstanceIdentifierData =
        {
            new InstanceCandidate
            {
                InstanceLabel = "object",
                LabelConfidenceLevel = 0f,
                Taxonomy = new InstanceTaxonomy
                {
                    TaxonomyLevelIDs = { "visual", "object" }
                }
            }
        }
    };
}
