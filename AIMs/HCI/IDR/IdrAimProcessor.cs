using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Hci.Idr;

// HCI-IDR-V1.0 - ID Reconciliation. Self-contained IAimProcessor.
// Reads its own port names from 1HCI-IDR-V1.0-I01.json at startup.
//
// Receives two Instance Identifiers of the SAME Data Type (OSD-IID-V1.5),
// distinguished by PortNumber: PortNumber 1 is the FACE identity (from PAF-FIR),
// PortNumber 2 is the SPEAKER identity (from MMC-SIR). Fuses their ranked
// candidates by score-level fusion (min-max normalise each, weighted-sum combine)
// and emits one reconciled OSD-IID.
//
// The fusion logic is the verified IdReconciliationAim unchanged; this processor
// bridges it to the AIF - reading the two IID ports by ordinal and packaging the
// reconciled identity as a Message. Either input may be absent (one modality
// only), in which case fusion degrades gracefully to the modality present.
public sealed class IdrAimProcessor : IAimProcessor
{
    private readonly string               _faceIdPort;
    private readonly string               _speakerIdPort;
    private readonly string               _outputPort;
    private readonly IdReconciliationAim  _idr;

    public string InstanceId { get; }

    public IdrAimProcessor(
        string instanceId,
        AimPortReader ports,
        double faceWeight = 0.5)
    {
        InstanceId     = instanceId;
        _idr           = new IdReconciliationAim(faceWeight);
        _faceIdPort    = ports.Input("OSD-IID-V1.5", 1);   // PortNumber 1 = face
        _speakerIdPort = ports.Input("OSD-IID-V1.5", 2);   // PortNumber 2 = speaker
        _outputPort    = ports.Output("OSD-IID-V1.5");
    }

    public async Task<Message> ProcessAsync(Message message)
    {
        var faceId    = ReadIdentity(message, _faceIdPort);
        var speakerId = ReadIdentity(message, _speakerIdPort);
        if (faceId is null && speakerId is null)
        {
            return Message.Error(message.MessageId, InstanceId,
                "HCI-IDR-V1.0: neither a face nor a speaker identity was delivered.");
        }
        InstanceIdentifier reconciled = _idr.ReconcileIdentifiers(faceId, speakerId);

        var json = MpaiJson.ToJson(reconciled);
        await Task.CompletedTask;

        return new Message
        {
            MessageId   = message.MessageId,
            MessageType = "InstanceIdentifier",
            DataType    = "OSD-IID-V1.5",
            Payload     = json,
            Ports       = new Dictionary<string, string> { [_outputPort] = json }
        };
    }

    private static InstanceIdentifier? ReadIdentity(Message message, string port) =>
        message.Ports.TryGetValue(port, out var json) && !string.IsNullOrWhiteSpace(json)
            ? MpaiJson.FromJson<InstanceIdentifier>(json)
            : null;
}
