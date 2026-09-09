using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;

using Mpai.Core;

namespace Mpai.Aims.Visual;

// AIF adapter for Visual Object Acquisition (CVE-VOA-V1.0).
//
// NOTE: Transitional 鈥?see TiqAimAdapter for the rationale.
// Port names must match 1CVE-VOA-V1.0-I01.json ExternalPorts.
public sealed class VoaAimAdapter
    : IAimProcessor
{
    public const string OutputPort = "OutputVisual";

    private readonly IVisualAcquisitionAim voa;
    private readonly string sourceHint;

    public string InstanceId { get; }

    public VoaAimAdapter(
        string instanceId,
        IVisualAcquisitionAim voa,
        string sourceHint = "")
    {
        InstanceId = instanceId;
        this.voa = voa;
        this.sourceHint = sourceHint;
    }

    public async Task<Message> ProcessAsync(
        Message message)
    {
        var image =
            await voa.AcquireAsync(
                new VisualAcquisitionRequest
                {
                    SourcePath = sourceHint
                });

        var json = MpaiJson.ToJson(image);

        return new Message
        {
            MessageId   = message.MessageId,
            MessageType = "BasicVisualObject",
            DataType    = image.Header,
            Payload     = json,
            Ports       = new Dictionary<string, string> { [OutputPort] = json }
        };
    }
}
