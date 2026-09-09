using System;
using System.Linq;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

using AIF.Controller;

using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Osd.VisualScene;   // ScrfdFaceDetector, FaceDetection
using Mpai.Paf.Fir;           // ArcFaceRecogniser, FaceCrop (shared primitives, pragmatic ref)

namespace Mpai.Paf.Efd;

// PAF-EFD-V1.6 - Entity Face Description, as an AIF IAimProcessor.
//
// Computes the Face Descriptors of an Entity from its Face Object: detects the
// most prominent face (SCRFD), crops it, embeds the crop (ArcFace), and emits a
// Face Descriptors Object (PAF-FDO) carrying that embedding, with a Qualifier
// recording the descriptor format. This is FIR's detect->crop->embed pipeline
// WITHOUT the gallery match - description produces the descriptor; recognition
// (or enrolment storage) is a separate step. Enrol and recognise therefore share
// exactly one feature-extraction path, so their embeddings are comparable.
public sealed class EfdAimProcessor : IAimProcessor
{
    // The descriptor format this implementation produces (a value from
    // TFA/V1.5/formats/FaceDescriptorsContentFormats.json).
    private const string ContentFormat = "ArcFace (ResNet-100, 512-d)";

    private readonly string _instanceId;
    private readonly ScrfdFaceDetector _detector;
    private readonly ArcFaceRecogniser _recogniser;

    private readonly string _inPort;
    private readonly string _outPort;
    private readonly string _timePort;
    private readonly string _namePort;
    private readonly AIF.SharedStorage.ISharedStorage _store;

    public EfdAimProcessor(
        string instanceId,
        ScrfdFaceDetector detector,
        ArcFaceRecogniser recogniser,
        AIF.SharedStorage.ISharedStorage store,
        AimPortReader ports)
    {
        _instanceId = instanceId;
        _detector   = detector;
        _recogniser = recogniser;
        _inPort     = ports.Input("OSD-BVO-V1.5");
        _outPort    = ports.Output("PAF-FDO-V1.6");
        _timePort   = ports.Input("OSD-STM-V1.5");      // acquisition time (OSD-STM)
        _namePort   = ports.InputOrDefault("OSD-BTO-V1.5", 1, string.Empty);   // subject name (UA-originated key), optional
        _store      = store;
    }

    public string InstanceId => _instanceId;

    public System.Threading.Tasks.Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(_inPort, out var bvoJson))
            return System.Threading.Tasks.Task.FromResult(
                Message.Error(message.MessageId, _instanceId, "no Face Object on input port"));

        var picture = MpaiJson.FromJson<BasicVisualObject>(bvoJson);
        if (picture is null || picture.Data.Length == 0)
            return System.Threading.Tasks.Task.FromResult(
                Message.Error(message.MessageId, _instanceId, "empty Face Object"));

        // Detect the most prominent face, crop, embed - the same path FIR uses.
        var faces = _detector.Detect(picture.Data);
        if (faces.Count == 0)
            return System.Threading.Tasks.Task.FromResult(
                Message.Error(message.MessageId, _instanceId, "no face detected"));

        var face = faces.OrderByDescending(f => f.Width * f.Height).First();
        using Image<Rgb24> crop = FaceCrop.Crop(picture.Data, face.X1, face.Y1, face.X2, face.Y2);
        var embedding = _recogniser.Embed(crop);

        var fdo = FaceDescriptorsObject.FromEmbedding(embedding, ContentFormat);

        // Stamp the acquisition time (OSD-STM) into the Face Descriptors Object.
        SimpleTime? faceTime = null;
        if (message.Ports.TryGetValue(_timePort, out var stmJson) && !string.IsNullOrWhiteSpace(stmJson))
            faceTime = MpaiJson.FromJson<SimpleTime>(stmJson);
        if (faceTime is not null)
            fdo = new FaceDescriptorsObject
            {
                FaceDescriptorsObjectID   = fdo.FaceDescriptorsObjectID,
                FaceDescriptorsObjectTime = faceTime,
                FaceDescriptorsData       = fdo.FaceDescriptorsData,
                FaceDescriptorsQualifier  = fdo.FaceDescriptorsQualifier
            };

        // Persist the FACE half to the gallery (Shared Storage, via the Controller API).
        if (message.Ports.TryGetValue(_namePort, out var nmJson) && !string.IsNullOrWhiteSpace(nmJson))
        {
            var name = MpaiJson.FromJson<BasicTextObject>(nmJson)?.GetText();
            if (!string.IsNullOrWhiteSpace(name))
            {
                var ftJson = fdo.FaceDescriptorsObjectTime is null ? null : MpaiJson.ToJson(fdo.FaceDescriptorsObjectTime);
                var g = Mpai.Core.SubjectGallery.Load(_store);
                g.EnrolEmbeddings(name!, face: embedding, faceTime: ftJson);
                g.Save(_store);
            }
        }

        return System.Threading.Tasks.Task.FromResult(new Message
        {
            MessageId = message.MessageId,
            MessageType = message.MessageType,
            Ports = new System.Collections.Generic.Dictionary<string, string>
            {
                [_outPort] = MpaiJson.ToJson(fdo)
            }
        });
    }
}
