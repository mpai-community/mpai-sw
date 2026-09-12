using System;
using System.Collections.Generic;

using AIF.Controller;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Mmc.Psm;

// MMC-PSM-V2.5 - Personal Status Multiplexing, as an AIF IAimProcessor.
//
// Assembles the per-modality Personal Statuses - Text (MMC-TPS), Speech (MMC-SPS),
// Face (MMC-FPS), and Gesture (MMC-GPS) - into a single Entity Personal Status
// (MMC-EPS). It combines; it does not compute. Each input is optional; at least one
// shall be present (an AIM with no input at all is skipped by the framework).
public sealed class PsmAimProcessor : IAimProcessor
{
    private readonly string _instanceId;
    private readonly string _textPort;    // MMC-TPS
    private readonly string _speechPort;  // MMC-SPS
    private readonly string _facePort;    // MMC-FPS
    private readonly string _gesturePort; // MMC-GPS
    private readonly string _outPort;     // MMC-EPS

    public PsmAimProcessor(string instanceId, AimPortReader ports)
    {
        _instanceId  = instanceId;
        _textPort    = ports.Input("MMC-TPS-V2.5");
        _speechPort  = ports.Input("MMC-SPS-V2.5");
        _facePort    = ports.Input("MMC-FPS-V2.5");
        _gesturePort = ports.Input("MMC-GPS-V2.5");
        _outPort     = ports.Output("MMC-EPS-V2.5");
    }

    public string InstanceId => _instanceId;

    public System.Threading.Tasks.Task<Message> ProcessAsync(Message message)
    {
        var eps = new EntityPersonalStatus
        {
            TextPersonalStatus    = Read<TextPersonalStatus>(message, _textPort),
            SpeechPersonalStatus  = Read<SpeechPersonalStatus>(message, _speechPort),
            FacePersonalStatus    = Read<FacePersonalStatus>(message, _facePort),
            GesturePersonalStatus = Read<GesturePersonalStatus>(message, _gesturePort)
        };

        if (eps.TextPersonalStatus is null && eps.SpeechPersonalStatus is null &&
            eps.FacePersonalStatus is null && eps.GesturePersonalStatus is null)
            return System.Threading.Tasks.Task.FromResult(
                Message.Error(message.MessageId, _instanceId, "no modality Personal Status on any input port"));

        return System.Threading.Tasks.Task.FromResult(new Message
        {
            MessageId = message.MessageId,
            MessageType = message.MessageType,
            Ports = new Dictionary<string, string> { [_outPort] = MpaiJson.ToJson(eps) }
        });
    }

    private static T? Read<T>(Message message, string port) where T : class
    {
        if (!message.Ports.TryGetValue(port, out var json) || string.IsNullOrWhiteSpace(json))
            return null;
        return MpaiJson.FromJson<T>(json);
    }
}
