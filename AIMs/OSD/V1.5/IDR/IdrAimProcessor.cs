using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using AIF.Controller;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Hci.Idr;   // IdReconciliationAim (the fusion logic, reused unchanged)

namespace Mpai.Osd.Idr;

// OSD-IDR-V1.5 - Identity Reconciliation. Self-contained IAimProcessor.
// Reads its own port names from 1OSD-IDR-V1.5-I01.json at startup - BY DATA TYPE.
//
// Reconciles the Face ID (from PAF-FIR) and the Speaker ID (from MMC-SIR) - two
// Instance Identifiers of the same Data Type (OSD-IID-V1.5), told apart by
// PortNumber (1 = face, 2 = speaker) - into ONE reconciled User ID, and issues
// the machine's Response (the words to speak) together with the Personal Status
// the avatar should display for the verdict:
//   granted (a recognised subject) -> welcoming
//   denied  (no recognised subject) -> reproaching
// (The serious status of the greeting/testing phase is spoken by the User Agent's
// guidance prompts, before this AIM runs; IDR issues the VERDICT's affect.)
//
// Outputs (by Data Type):
//   UserID         (OSD-IID-V1.5) - the reconciled identity
//   PersonalStatus (MMC-EPS-V2.5) - welcoming | reproaching, for Response & Scene Rendering
//   Response       (OSD-BTO-V1.5) - the verdict words the avatar speaks
public sealed class IdrAimProcessor : IAimProcessor
{
    private readonly string              _faceIdPort;
    private readonly string              _speakerIdPort;
    private readonly string              _userIdPort;
    private readonly string              _personalStatusPort;
    private readonly string              _responsePort;
    private readonly IdReconciliationAim _idr;

    public string InstanceId { get; }

    public IdrAimProcessor(
        string instanceId,
        AimPortReader ports,
        double faceWeight = 0.5)
    {
        InstanceId          = instanceId;
        _idr                = new IdReconciliationAim(faceWeight);
        _faceIdPort         = ports.Input("OSD-IID-V1.5", 1);   // PortNumber 1 = face
        _speakerIdPort      = ports.Input("OSD-IID-V1.5", 2);   // PortNumber 2 = speaker
        _userIdPort         = ports.Output("OSD-IID-V1.5");
        _personalStatusPort = ports.Output("MMC-EPS-V2.5");
        _responsePort       = ports.Output("OSD-BTO-V1.5");
    }

    public async Task<Message> ProcessAsync(Message message)
    {
        var faceId    = ReadIdentity(message, _faceIdPort);
        var speakerId = ReadIdentity(message, _speakerIdPort);

        // Reconcile (may be one modality only; degrades gracefully).
        InstanceIdentifier reconciled = _idr.ReconcileIdentifiers(faceId, speakerId);
        System.IO.File.AppendAllText(@"C:\Users\Leonardo\Downloads\mac-diag.log", "[IDR] faceId=" + (faceId == null || faceId.InstanceIdentifierData == null || faceId.InstanceIdentifierData.Count == 0 ? "nil" : faceId.InstanceIdentifierData[0].InstanceLabel) + " speakerId=" + (speakerId == null || speakerId.InstanceIdentifierData == null || speakerId.InstanceIdentifierData.Count == 0 ? "nil" : speakerId.InstanceIdentifierData[0].InstanceLabel) + System.Environment.NewLine);

        // Decide: granted when the reconciled top candidate is a REAL subject -
        // a named identity, not the coarse "person"/"face"/"speech" fallback.
        var top = reconciled.InstanceIdentifierData.FirstOrDefault();
        string? subject = top?.InstanceLabel;
        bool granted = subject is not null
                       && !string.IsNullOrWhiteSpace(subject)
                       && !IsCoarse(subject);

        string responseText = granted
            ? $"Access granted. Welcome, {subject}."
            : "I'm sorry, I could not recognise you. Access denied.";

        EntityPersonalStatus ps = granted
            ? MachinePersonalStatus("HAPPINESS", "welcoming")
            : MachinePersonalStatus("ANGER", "reproaching");

        var userIdJson   = MpaiJson.ToJson(reconciled);
        var psJson       = MpaiJson.ToJson(ps);
        var responseJson = MpaiJson.ToJson(BasicTextObject.FromText(responseText));
        await Task.CompletedTask;

        return new Message
        {
            MessageId   = message.MessageId,
            MessageType = "InstanceIdentifier",
            DataType    = "OSD-IID-V1.5",
            Payload     = userIdJson,
            Ports       = new Dictionary<string, string>
            {
                [_userIdPort]         = userIdJson,
                [_personalStatusPort] = psJson,
                [_responsePort]       = responseJson
            }
        };
    }

    private static bool IsCoarse(string label) =>
        label is "person" or "face" or "speech";

    private static InstanceIdentifier? ReadIdentity(Message message, string port) =>
        message.Ports.TryGetValue(port, out var json) && !string.IsNullOrWhiteSpace(json)
            ? MpaiJson.FromJson<InstanceIdentifier>(json)
            : null;

    // The machine's own Personal Status for the verdict, carried in the Text
    // modality for Personal Status De-multiplexing (inside RSR) to pick up -
    // the same shape Entity Dialogue Processing produces.
    private static EntityPersonalStatus MachinePersonalStatus(string emotion, string attitude)
    {
        FactorLabel emo = emotion.ToUpperInvariant() switch
        {
            "HAPPINESS" => FactorLabel.Of("HAPPINESS", "happy", null, 0.8),
            "ANGER"     => FactorLabel.Of("ANGER", "stern", null, 0.7),
            _           => FactorLabel.Of("CALMNESS", "calm", null, 0.6)
        };
        SocialAttitude att = attitude.ToLowerInvariant() switch
        {
            "welcoming"   => SocialAttitude.Of(FactorLabel.Of("ACCEPTANCE", "welcoming", null, 0.8)),
            "reproaching" => SocialAttitude.Of(FactorLabel.Of("SOCIAL RANK", "reproaching", null, 0.7)),
            _             => SocialAttitude.Of(FactorLabel.Of("ACCEPTANCE", "neutral", null, 0.5))
        };
        return new EntityPersonalStatus
        {
            TextPersonalStatus = new TextPersonalStatus
            {
                TextEmotion        = Emotion.Of(emo),
                TextSocialAttitude = att
            }
        };
    }
}
