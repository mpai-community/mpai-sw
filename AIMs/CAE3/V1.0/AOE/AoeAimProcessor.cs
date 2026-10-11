using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Cae.Aoe;

// CAE-AOE-V1.0 - Audio Object Editing, as the author's Reference Model of Audio Scene
// Management draws it (CAE Reference Models, 2026/10/04): User Command, User PoV and
// a Basic Audio Object in; the Basic Audio Object as edited, and the User Command
// with its report, out. The Object goes on to Audio Scene Editing.
//
// OPEN BY PORT, ACT BY COMMAND. One Object is open at a time, so no Command has to
// name its target. A Basic Audio Object arriving is opened when Shared Storage holds
// it (by its identifier), and created when it is new - a sound the User Agent
// captured. Then a User Command acts on what is open:
//
//   ModifiedObjects   what it is like: loudness (LUFS) and the Object part of the
//                     Acoustic Profile
//   UserPoV           where it is heard from, when heard on its own
//
// Every edit is a new version in Shared Storage. Where an Object is in a Scene is
// Audio Scene Editing's (AddedObjects, MovedObjects, ChangedObjects): a command
// naming those here is reported as not supported, not silently dropped.
public sealed class AoeAimProcessor : IAimProcessor
{
    private readonly AoeAim _aoe;

    private readonly string _commandPort;
    private readonly string _povPort;
    private readonly string _objectPort;
    private readonly string _objectOut;
    private readonly string _reportOut;

    // What is open, between runs.
    private string? _open;

    public string InstanceId { get; }

    public AoeAimProcessor(string instanceId, AoeAim aoe, AimPortReader ports)
    {
        InstanceId   = instanceId;
        _aoe         = aoe;
        _commandPort = ports.Input("CAE-UCM-V1.0");
        _povPort     = ports.InputOrDefault("OSD-OPV-V1.5", "");
        _objectPort  = ports.Input("OSD-BAO-V1.5");
        _objectOut   = ports.Output("OSD-BAO-V1.5");
        _reportOut   = ports.OutputOrDefault("CAE-UCM-V1.0", "");
    }

    public Task<Message> ProcessAsync(Message message)
    {
        var outputs = new Dictionary<string, string>();

        // 1. opening, or creating, by Port.
        if (message.Ports.TryGetValue(_objectPort, out var objectJson) && !string.IsNullOrWhiteSpace(objectJson))
        {
            var basic = MpaiJson.FromJson<BasicAudioObject>(objectJson);
            _open = _aoe.Has(basic.BasicAudioObjectID) ? basic.BasicAudioObjectID : _aoe.CreateObject(basic).AssetId;
            Console.WriteLine($"[CAE-AOE-V1.0] open {_open}");
        }

        // 2. a User PoV on its Port: where the open Object is heard from.
        if (_povPort.Length > 0 && _open is not null && message.Ports.TryGetValue(_povPort, out var povJson) && !string.IsNullOrWhiteSpace(povJson))
            _open = _aoe.EditBasicObjectProperties(_open, listenerPointOfView: MpaiJson.FromJson<PointOfView>(povJson)).AssetId;

        // 3. acting, by Command; the report goes back under the command's identifier.
        if (message.Ports.TryGetValue(_commandPort, out var commandJson) && !string.IsNullOrWhiteSpace(commandJson))
        {
            var command = MpaiJson.FromJson<UserCommand>(commandJson);
            var report = new CommandReport(command);
            Apply(command, report);
            if (_reportOut.Length > 0) outputs[_reportOut] = MpaiJson.ToJson(report.Report());
        }

        // 4. the open Object, as it now stands.
        if (_open is not null) outputs[_objectOut] = MpaiJson.ToJson(_aoe.Get(_open));
        return Task.FromResult(new Message
        {
            MessageId   = Guid.NewGuid().ToString(),
            MessageType = _open is null ? "NoOpenObject" : "BasicAudioObject",
            DataType    = _open is null ? "" : "OSD-BAO-V1.5",
            Ports       = outputs
        });
    }

    private void Apply(UserCommand command, CommandReport report)
    {
        var data = command.UserCommandData;
        if (data is null) return;
        const string inScene = "where an Object is in a Scene is Audio Scene Editing's";
        foreach (var (action, any) in new[] { ("AddedObjects", data.AddedObjects is { Objects.Count: > 0 }),
                                              ("MovedObjects", data.MovedObjects is { Objects.Count: > 0 }),
                                              ("ChangedObjects", data.ChangedObjects is { Objects.Count: > 0 }),
                                              ("RemovedObjects", data.RemovedObjects is { Objects.Count: > 0 }) })
            if (any) report.NotSupported(action, _open, inScene);
        if (data.TranslatedObjects is { Objects.Count: > 0 })
            report.NotSupported("TranslatedObjects", _open, "speech is translated by Speech Object Editing");

        if (_open is null)
        {
            if (data.UserPoV is not null || data.ModifiedObjects is { Objects.Count: > 0 } || data.LUFS is not null)
                report.Failed("ModifiedObjects", null, "no Basic Audio Object is open");
            return;
        }

        if (data.UserPoV is not null)
        {
            var before = _open;
            _open = _aoe.EditBasicObjectProperties(_open, listenerPointOfView: data.UserPoV).AssetId;
            report.Done("UserPoV", before, _open);
        }

        // What the Object is like: its loudness and its Acoustic Profile (Object part).
        if (data.ModifiedObjects is { Objects.Count: > 0 } modified)
            foreach (var entry in modified.Objects)
            {
                var before = _open;
                _open = _aoe.EditBasicObjectProperties(_open, level: data.LUFS, acousticProfile: entry.AcousticProfile).AssetId;
                report.Done("ModifiedObjects", before, _open);
            }
        else if (data.LUFS is { } lufs)
        {
            var before = _open;
            _open = _aoe.EditBasicObjectProperties(_open, level: lufs).AssetId;
            report.Done("LUFS", before, _open);
        }
    }
}
