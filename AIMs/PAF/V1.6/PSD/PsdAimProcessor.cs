using System;
using System.Collections.Generic;

using AIF.Controller;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Paf.Psd;

// PAF-PSD-V1.6 - Personal Status De-multiplexing, as an AIF IAimProcessor.
//
// The inverse of Personal Status Multiplexing (MMC-PSM). Takes one Entity Personal
// Status (the machine's, from Entity Dialogue Processing) and splits it into the
// per-modality Personal Statuses - Speech, Face, and Gesture - which the Response
// and Scene Rendering composite then uses to render the speaking avatar (affective
// Text-To-Speech, facial expression, gesture). It de-multiplexes; it does not
// compute. A modality absent from the Entity Personal Status is emitted as null.
//
// This first implementation reads the machine's Personal Status factors from
// whichever modality slot carries them (Text/Speech/Face) and projects them onto
// the three output modalities, so an Entity Personal Status that only carried a
// Text modality (as EDP produces) still drives speech, face, and gesture.
public sealed class PsdAimProcessor : IAimProcessor
{
    private readonly string _instanceId;
    private readonly string _inPort;       // MMC-EPS
    private readonly string _speechPsPort; // MMC-SPS
    private readonly string _facePsPort;   // MMC-FPS
    private readonly string _gesturePsPort;// MMC-GPS

    public PsdAimProcessor(string instanceId, AimPortReader ports)
    {
        _instanceId    = instanceId;
        _inPort        = ports.Input("MMC-EPS-V2.5");
        _speechPsPort  = ports.Output("MMC-SPS-V2.5");
        _facePsPort    = ports.Output("MMC-FPS-V2.5");
        _gesturePsPort = ports.Output("MMC-GPS-V2.5");
    }

    public string InstanceId => _instanceId;

    public System.Threading.Tasks.Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(_inPort, out var epsJson) || string.IsNullOrWhiteSpace(epsJson))
            return System.Threading.Tasks.Task.FromResult(
                Message.Error(message.MessageId, _instanceId, "no Entity Personal Status on input port"));

        var eps = MpaiJson.FromJson<EntityPersonalStatus>(epsJson);
        if (eps is null)
            return System.Threading.Tasks.Task.FromResult(
                Message.Error(message.MessageId, _instanceId, "could not read Entity Personal Status"));

        // Resolve the machine's factors from whichever modality slot carried them
        // (EDP emits them in the Text slot), then project onto each output modality.
        var (cog, emo, att) = ResolveFactors(eps);

        var sps = new SpeechPersonalStatus
        {
            SpeechCognitiveState = cog, SpeechEmotion = emo, SpeechSocialAttitude = att
        };
        var fps = new FacePersonalStatus
        {
            FaceCognitiveState = cog, FaceEmotion = emo, FaceSocialAttitude = att
        };
        var gps = new GesturePersonalStatus
        {
            GestureCognitiveState = cog, GestureEmotion = emo, GestureSocialAttitude = att
        };

        return System.Threading.Tasks.Task.FromResult(new Message
        {
            MessageId = message.MessageId,
            MessageType = message.MessageType,
            Ports = new Dictionary<string, string>
            {
                [_speechPsPort]  = MpaiJson.ToJson(sps),
                [_facePsPort]    = MpaiJson.ToJson(fps),
                [_gesturePsPort] = MpaiJson.ToJson(gps)
            }
        });
    }

    // Pull the three factors from whichever modality Personal Status is present in
    // the Entity Personal Status (Text first - EDP's slot - then Speech, then Face).
    private static (CognitiveState?, Emotion?, SocialAttitude?) ResolveFactors(EntityPersonalStatus eps)
    {
        if (eps.TextPersonalStatus is { } t)
            return (t.TextCognitiveState, t.TextEmotion, t.TextSocialAttitude);
        if (eps.SpeechPersonalStatus is { } s)
            return (s.SpeechCognitiveState, s.SpeechEmotion, s.SpeechSocialAttitude);
        if (eps.FacePersonalStatus is { } f)
            return (f.FaceCognitiveState, f.FaceEmotion, f.FaceSocialAttitude);
        if (eps.GesturePersonalStatus is { } g)
            return (g.GestureCognitiveState, g.GestureEmotion, g.GestureSocialAttitude);
        return (null, null, null);
    }
}
