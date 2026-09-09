using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;
using Mpai.Core;

namespace Mpai.Aims.Visual;

// CVE-VOA-V1.0 — self-contained IAimProcessor.
// Reads its own port names from 1CVE-VOA-V1.0-I01.json at startup.
//
// Zero-trust input handling: if a Visual Object was delivered to VOA's input
// port (piped by the Controller from a composite boundary), VOA USES it. Only
// when no input is present does VOA acquire from its device/file source (the
// case where VOA is the genuine entry point acquiring fresh visual data).
public sealed class VoaAimProcessor : IAimProcessor
{
    private readonly string                _inputPort;
    private readonly string                _outputPort;
    private readonly IVisualAcquisitionAim _voa;
    private readonly string                _sourceHint;

    public string InstanceId { get; }

    public VoaAimProcessor(
        string                instanceId,
        IVisualAcquisitionAim voa,
        AimPortReader              ports,
        string                sourceHint = "")
    {
        InstanceId   = instanceId;
        _voa         = voa;
        _sourceHint  = sourceHint;
        _inputPort   = ports.InputOrDefault("OSD-VIO-V1.5", "InputVisual");
        _outputPort  = ports.Output("OSD-VIO-V1.5");
    }

    public async Task<Message> ProcessAsync(Message message)
    {
        // Use the piped input image if the Controller delivered one.
        string json;
        if (message.Ports.TryGetValue(_inputPort, out var suppliedJson) &&
            !string.IsNullOrWhiteSpace(suppliedJson))
        {
            json = suppliedJson;
        }
        else
        {
            // No input delivered — acquire fresh from the device/file source.
            var image = await _voa.AcquireAsync(
                new VisualAcquisitionRequest { SourcePath = _sourceHint });
            json = MpaiJson.ToJson(image);
        }

        return new Message
        {
            MessageId   = message.MessageId,
            MessageType = "BasicVisualObject",
            DataType    = "OSD-VIO-V1.5",
            Payload     = json,
            Ports       = new Dictionary<string, string> { [_outputPort] = json }
        };
    }
}
