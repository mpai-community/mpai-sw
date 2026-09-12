using System;
using System.Collections.Generic;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

using AIF.Controller;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Mmc.Efi;

// MMC-EFI-V2.5 - Entity Face Interpretation, as an AIF IAimProcessor.
//
// Receives a Basic Visual Object (OSD-BVO) that IS a face - a face already
// isolated upstream (the Face Object), the visual-object mirror of ESI's Speech
// Object - and produces the Face Personal Status (MMC-FPS): the Personal Status
// Factors carried by the face, each a chosen label + Degree.
//
// It INTERPRETS the given face directly with HSEmotion (EfficientNet-B0 multi-
// task, AffectNet); it does NOT DETECT a face in a scene. Detection is scene
// work (locating a face in a frame) and belongs upstream; EFI, like ESI, reads
// affect from the object it is given. HSEmotion resizes the image to 224x224 and
// reads eight emotion probabilities plus valence and arousal. The chosen emotion
// maps to an MPAI Emotion label (MMC-EEM); Surprise, a Cognitive State in MPAI,
// is emitted as a Cognitive State (MMC-ECS). The Degree is the model confidence.
public sealed class EfiAimProcessor : IAimProcessor
{
    private readonly string _instanceId;
    private readonly HSEmotionEstimator _hse;
    private readonly string _inPort;   // OSD-BVO
    private readonly string _outPort;  // MMC-FPS

    public EfiAimProcessor(string instanceId, HSEmotionEstimator hse, AimPortReader ports)
    {
        _instanceId = instanceId;
        _hse        = hse;
        _inPort     = ports.Input("OSD-BVO-V1.5");
        _outPort    = ports.Output("MMC-FPS-V2.5");
    }

    public string InstanceId => _instanceId;

    public System.Threading.Tasks.Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(_inPort, out var bvoJson) || string.IsNullOrWhiteSpace(bvoJson))
            return System.Threading.Tasks.Task.FromResult(
                Message.Error(message.MessageId, _instanceId, "no Basic Visual Object on input port"));

        var visual = MpaiJson.FromJson<BasicVisualObject>(bvoJson);
        if (visual is null || visual.Data.Length == 0)
            return System.Threading.Tasks.Task.FromResult(
                Message.Error(message.MessageId, _instanceId, "empty Basic Visual Object"));

        // Interpret the given face directly (HSEmotion resizes to 224x224). No
        // detection: the input is already a face (the Face Object).
        FaceAffect affect;
        using (var image = Image.Load<Rgb24>(visual.Data))
            affect = _hse.Estimate(image);

        var fps = ToFacePersonalStatus(affect);

        return System.Threading.Tasks.Task.FromResult(new Message
        {
            MessageId = message.MessageId,
            MessageType = message.MessageType,
            Ports = new Dictionary<string, string> { [_outPort] = MpaiJson.ToJson(fps) }
        });
    }

    private static FacePersonalStatus ToFacePersonalStatus(FaceAffect a)
    {
        double degree = Math.Clamp(a.Confidence, 0.0, 1.0);

        if (a.Emotion == "Surprise")
            return new FacePersonalStatus
            {
                FaceCognitiveState = CognitiveState.Of(FactorLabel.Of("SURPRISE", "surprised", null, degree))
            };

        FactorLabel label = a.Emotion switch
        {
            "Anger"     => FactorLabel.Of("ANGER", "angry", null, degree),
            "Disgust"   => FactorLabel.Of("DISGUST", "disgusted", null, degree),
            "Fear"      => FactorLabel.Of("FEAR", "fearful/scared", null, degree),
            "Happiness" => FactorLabel.Of("HAPPINESS", "happy", null, degree),
            "Sadness"   => FactorLabel.Of("SADNESS", "sad", null, degree),
            "Contempt"  => FactorLabel.Of("HURT", "hurt", null, degree),
            _           => FactorLabel.Of("CALMNESS", "calm", null, degree)   // Neutral
        };

        return new FacePersonalStatus { FaceEmotion = Emotion.Of(label) };
    }
}
