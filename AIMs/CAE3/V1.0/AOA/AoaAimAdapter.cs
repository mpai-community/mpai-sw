using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;

using Mpai.Core;

namespace Mpai.Aims.Audio;

// AIF adapter for Audio Object Acquisition (CAE-AOA-V1.0).
//
// NOTE: Transitional 鈥?see TiqAimAdapter for the rationale.
// Port names must match 1CAE-AOA-V1.0-I01.json ExternalPorts.
//
// Implements IStartStopAcquisition by delegating to the underlying
// IAudioAcquisitionAim if it supports start/stop (e.g. WasapiAudioAcquisition).
// This allows AifAmqSession to call StartListening/StopAndAnswerAsync via
// AimHost.TryGetProcessor<IStartStopAcquisition>.
public sealed class AoaAimAdapter
    : IAimProcessor, IStartStopAcquisition
{
    public const string OutputPort = "OutputAudio";

    private readonly IAudioAcquisitionAim  _aoa;
    private readonly IStartStopAcquisition? _startStop;
    private readonly TimeSpan              _duration;

    public string InstanceId { get; }

    public AoaAimAdapter(
        string instanceId,
        IAudioAcquisitionAim aoa,
        TimeSpan? duration = null)
    {
        InstanceId  = instanceId;
        _aoa        = aoa;
        _startStop  = aoa as IStartStopAcquisition;
        _duration   = duration ?? TimeSpan.FromSeconds(5);
    }

    // IAimProcessor 鈥?used by MachineExecutor in headless mode.
    public async Task<Message> ProcessAsync(Message message)
    {
        var audio = await _aoa.AcquireAsync(
            new AcquisitionRequest { Duration = _duration });

        var json = MpaiJson.ToJson(audio);

        return new Message
        {
            MessageId   = message.MessageId,
            MessageType = "BasicAudioObject",
            DataType    = audio.Header,
            Payload     = json,
            Ports       = new Dictionary<string, string> { [OutputPort] = json }
        };
    }

    // IStartStopAcquisition 鈥?used by AifAmqSession in attended mode.
    public void StartAcquire()
    {
        if (_startStop is null)
            throw new NotSupportedException(
                $"{_aoa.GetType().Name} does not support start/stop acquisition.");
        _startStop.StartAcquire();
    }

    public Task<BasicAudioObject> StopAcquireAsync()
    {
        if (_startStop is null)
            throw new NotSupportedException(
                $"{_aoa.GetType().Name} does not support start/stop acquisition.");
        return _startStop.StopAcquireAsync();
    }
}
