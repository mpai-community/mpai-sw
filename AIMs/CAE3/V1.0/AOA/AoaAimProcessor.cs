using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;
using Mpai.Core;

namespace Mpai.Aims.Audio;

// CAE-AOA-V1.0 - self-contained IAimProcessor.
// Reads its own port names from 1CAE-AOA-V1.0-I01.json at startup.
//
// Zero-trust input handling: if an Audio Object was delivered to AOA's input
// port (piped by the Controller from a composite boundary), AOA USES it. Only
// when no input is present does AOA acquire from its device (attended
// start/stop, or headless timed) - the case where AOA is the genuine entry
// point acquiring fresh audio.
public sealed class AoaAimProcessor : IAimProcessor
{
    private readonly string                 _inputPort;
    private readonly string                 _outputPort;
    private readonly IAudioAcquisitionAim   _aoa;
    private readonly IStartStopAcquisition? _startStop;
    private readonly System.TimeSpan        _duration;

    public string InstanceId { get; }

    public AoaAimProcessor(
        string               instanceId,
        IAudioAcquisitionAim aoa,
        AimPortReader             ports,
        System.TimeSpan?     duration = null)
    {
        InstanceId  = instanceId;
        _aoa        = aoa;
        _startStop  = aoa as IStartStopAcquisition;
        _duration   = duration ?? System.TimeSpan.FromSeconds(5);
        _inputPort  = ports.InputOrDefault("OSD-AUO-V1.5", "InputAudio");
        _outputPort = ports.Output("OSD-AUO-V1.5");
    }

    public async Task<Message> ProcessAsync(Message message)
    {
        // Use the piped input audio if the Controller delivered one.
        if (message.Ports.TryGetValue(_inputPort, out var suppliedJson) &&
            !string.IsNullOrWhiteSpace(suppliedJson))
        {
            return new Message
            {
                MessageId   = message.MessageId,
                MessageType = "BasicAudioObject",
                DataType    = "OSD-AUO-V1.5",
                Payload     = suppliedJson,
                Ports       = new Dictionary<string, string> { [_outputPort] = suppliedJson }
            };
        }

        // No input delivered - acquire fresh.
        var context = message.Context;
        BasicAudioObject audio;

        if (_startStop is not null &&
            context.StopToken != System.Threading.CancellationToken.None)
        {
            _startStop.StartAcquire();
            try
            {
                await Task.Delay(
                    System.Threading.Timeout.Infinite,
                    context.StopToken);
            }
            catch (System.OperationCanceledException)
            {
                // StopToken fired - expected.
            }
            audio = await _startStop.StopAcquireAsync();
        }
        else
        {
            audio = await _aoa.AcquireAsync(
                new AcquisitionRequest { Duration = _duration });
        }

        var json = MpaiJson.ToJson(audio);

        return new Message
        {
            MessageId   = message.MessageId,
            MessageType = "BasicAudioObject",
            DataType    = "OSD-AUO-V1.5",
            Payload     = json,
            Ports       = new Dictionary<string, string> { [_outputPort] = json }
        };
    }
}
