using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

using AIF.Controller;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Osd.VisualScene;   // ScrfdFaceDetector, FaceDetection
using OsdBoundingBox = Mpai.Core.OSD.BoundingBox;   // disambiguate from the text BoundingBox in Mpai.Core

namespace Mpai.Paf.Fir;

// PAF-FIR-V1.6 - Face Identity Recognition. Self-contained IAimProcessor.
// Reads its own port names from 1PAF-FIR-V1.6-I01.json at startup.
//
// Receives a Basic Visual Object (the picture), its Face Time, and the Basic
// Visual Scene Descriptors as context. In the single-user case the App asks
// simply "the identity of the user": FIR detects the face with SCRFD (taking the
// most prominent one when several are present), crops it, embeds it with ArcFace,
// matches it against the shared SubjectGallery, and returns:
//   - FaceID     (OSD-IID-V1.5): the person identity, a ranked candidate list, or
//                                the coarse "face" layer when no subject matches;
//   - BoundingBox(OSD-BBX-V1.5): WHERE that face is, carrying its JPEG crop as
//                                Visual Data, so a downstream AIM knows which face
//                                the identity belongs to.
//
// Format is qualifier-borne: the input BVO's VisualQualifier declares its format
// (JPEG) and the emitted crop records JPEG in both its VisualQualifier and the
// BBX content format - the image bytes are never assumed to be a bare raster.
public sealed class FirAimProcessor : IAimProcessor
{
    private readonly string                     _visualPort;
    private readonly string                     _iidPort;
    private readonly string                     _bbxPort;
    private readonly ScrfdFaceDetector          _detector;
    private readonly ArcFaceRecogniser          _recogniser;
    private readonly SubjectGallery             _gallery;

    public string InstanceId { get; }

    public FirAimProcessor(
        string instanceId,
        ScrfdFaceDetector detector,
        ArcFaceRecogniser recogniser,
        SubjectGallery gallery,
        AimPortReader ports)
    {
        InstanceId  = instanceId;
        _detector   = detector;
        _recogniser = recogniser;
        _gallery    = gallery;
        _visualPort = ports.Input("OSD-BVO-V1.5");
        _iidPort    = ports.Output("OSD-IID-V1.5");
        _bbxPort    = ports.Output("OSD-BBX-V1.5");
    }

    public async Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(_visualPort, out var visualJson) ||
            string.IsNullOrWhiteSpace(visualJson))
        {
            return Message.Error(message.MessageId, InstanceId,
                $"PAF-FIR-V1.6: no visual object on input port '{_visualPort}'.");
        }

        var picture = MpaiJson.FromJson<BasicVisualObject>(visualJson);
        if (picture is null || picture.Data is null || picture.Data.Length == 0)
        {
            return Message.Error(message.MessageId, InstanceId,
                "PAF-FIR-V1.6: visual object carried no image data.");
        }

        // Type guard: FIR identifies FACES only. CVE-VSI tags each object's
        // VisualQualifier with VisualObjectType; both FIR and VII receive the
        // OSD-BVO stream (routing is by data type) and each acts only on its type,
        // so EXACTLY ONE Instance Identifier is produced per object. FIR acts only
        // on "Face"; anything else (or untagged) it declines - VII identifies it.
        var visualObjectType = picture.VisualQualifier?.Attributes?.VisualObjectType;
        if (visualObjectType != "Face")
        {
            return new Message
            {
                MessageId   = message.MessageId,
                MessageType = message.MessageType,
                Ports       = new Dictionary<string, string>()
            };
        }

        // Detect faces (SCRFD separates them); take the user's face = the most
        // prominent (largest area) when several are present.
        var faces = _detector.Detect(picture.Data);
        System.IO.File.AppendAllText(@"C:\Users\Leonardo\Downloads\mac-diag.log", "[FIR] type=" + (picture.VisualQualifier?.Attributes?.VisualObjectType ?? "nil") + " bytes=" + (picture.Data == null ? 0 : picture.Data.Length) + " faces=" + faces.Count + System.Environment.NewLine);
        if (faces.Count == 0)
        {
            // No face found: emit a coarse "no identity" IID and an empty BBX so
            // the two output ports are still satisfied.
            var none = CoarseFaceIdentity();
            var emptyJson = MpaiJson.ToJson(none);
            return new Message
            {
                MessageId   = message.MessageId,
                MessageType = "InstanceIdentifier",
                DataType    = "OSD-IID-V1.5",
                Payload     = emptyJson,
                Ports       = new Dictionary<string, string> { [_iidPort] = emptyJson }
            };
        }

        var face = faces.OrderByDescending(f => f.Width * f.Height).First();

        // Crop the face and identify it.
        using Image<Rgb24> crop = FaceCrop.Crop(picture.Data, face.X1, face.Y1, face.X2, face.Y2);
        InstanceIdentifier identity = _recogniser is not null
            ? IdentifyCrop(crop)
            : CoarseFaceIdentity();

        // Build the BBX: the face's location, carrying its JPEG crop as Visual Data.
        byte[] cropJpeg = EncodeJpeg(crop);
        var faceObject = new BasicVisualObject
        {
            BasicVisualObjectID = System.Guid.NewGuid().ToString(),
            Data = cropJpeg,
            VisualQualifier = VisualQualifier.For2DStill(Visual2DStaticFormat.JPEG)
        };
        var bbx = OsdBoundingBox.For2DFace(faceObject);

        var iidJson = MpaiJson.ToJson(identity);
        var bbxJson = MpaiJson.ToJson(bbx);
        await Task.CompletedTask;

        return new Message
        {
            MessageId   = message.MessageId,
            MessageType = "InstanceIdentifier",
            DataType    = "OSD-IID-V1.5",
            Payload     = iidJson,
            Ports       = new Dictionary<string, string>
            {
                [_iidPort] = iidJson,
                [_bbxPort] = bbxJson
            }
        };
    }

    // Embed the crop and match the gallery -> layered person IID (or coarse "face").
    private InstanceIdentifier IdentifyCrop(Image<Rgb24> crop)
    {
        var fir = new FaceIdentityRecognitionAim(_recogniser, _gallery);
        var iid = fir.Identify(crop);
        try { var __c = iid?.InstanceIdentifierData; var __top = (__c != null && __c.Count > 0) ? __c[0] : null; System.IO.File.AppendAllText(@"C:\Users\Leonardo\Downloads\mac-diag.log", "[FIR] IID label=" + (__top == null ? "nil" : __top.InstanceLabel) + " conf=" + (__top == null ? "nil" : __top.LabelConfidenceLevel.ToString("F3")) + " candidates=" + (__c == null ? 0 : __c.Count) + System.Environment.NewLine); } catch { }
        return iid;
    }

    private static InstanceIdentifier CoarseFaceIdentity() => new()
    {
        InstanceIdentifier_ = System.Guid.NewGuid().ToString(),
        InstanceIdentifierData = new List<InstanceCandidate>
        {
            new InstanceCandidate
            {
                InstanceLabel = "face",
                LabelConfidenceLevel = 1.0,
                Taxonomy = new InstanceTaxonomy
                {
                    TaxonomyLevelIDs = new List<string> { "visual", "face" }
                }
            }
        }
    };

    private static byte[] EncodeJpeg(Image<Rgb24> image)
    {
        using var ms = new System.IO.MemoryStream();
        image.SaveAsJpeg(ms);
        return ms.ToArray();
    }
}
