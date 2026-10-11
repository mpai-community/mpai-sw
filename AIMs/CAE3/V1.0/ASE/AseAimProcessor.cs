using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Cae.Ase;

// CAE-ASE-V1.0 - Audio Scene Editing, as the author's Reference Model of Audio Scene
// Management draws it (CAE Reference Models, 2026/10/04): a Basic Audio Object (from
// Audio Object Editing), a Basic Audio Scene, a User Command and a User PoV in; the
// Basic Audio Scene as edited, and the User Command with its report, out.
//
// OPEN BY PORT, ACT BY COMMAND. One Scene is open at a time, so no Command names it.
// A Basic Audio Scene arriving is opened. A User Command acts on the open Scene - the
// first Object added to no open Scene creates one:
//
//   AddedObjects     place the named Basic Audio Objects
//   MovedObjects     old attitude -> new attitude
//   ChangedObjects   where a placed Object is
//   UserPoV          where the Scene is heard from (also on its own Port)
//
//   RemovedObjects   take placed Objects out
//
// Every edit is a new version in Shared Storage. The Objects are read from Shared
// Storage by their identifiers; what Audio Object Editing leaves is there.
public sealed class AseAimProcessor : IAimProcessor
{
    private readonly AseAim _ase;

    private readonly string _commandPort;
    private readonly string _objectPort;
    private readonly string _scenePort;
    private readonly string _povPort;
    private readonly string _sceneOut;
    private readonly string _reportOut;

    // What is open, between runs.
    private string? _open;

    public string InstanceId { get; }

    public AseAimProcessor(string instanceId, AseAim ase, AimPortReader ports)
    {
        InstanceId   = instanceId;
        _ase         = ase;
        _commandPort = ports.Input("CAE-UCM-V1.0");
        _objectPort  = ports.InputOrDefault("OSD-BAO-V1.5", "");
        _scenePort   = ports.Input("OSD-BAS-V1.5");
        _povPort     = ports.InputOrDefault("OSD-OPV-V1.5", "");
        _sceneOut    = ports.Output("OSD-BAS-V1.5");
        _reportOut   = ports.OutputOrDefault("CAE-UCM-V1.0", "");
    }

    public Task<Message> ProcessAsync(Message message)
    {
        var outputs = new Dictionary<string, string>();

        // 1. opening, by Port.
        if (message.Ports.TryGetValue(_scenePort, out var sceneJson) && !string.IsNullOrWhiteSpace(sceneJson))
        {
            var id = MpaiJson.FromJson<BasicAudioSceneDescriptors>(sceneJson).BasicAudioSceneDescriptorsID;
            if (!string.IsNullOrWhiteSpace(id)) { _open = id; Console.WriteLine($"[CAE-ASE-V1.0] opened {_open}"); }
        }

        PointOfView? pov = null;
        if (_povPort.Length > 0 && message.Ports.TryGetValue(_povPort, out var povJson) && !string.IsNullOrWhiteSpace(povJson))
            pov = MpaiJson.FromJson<PointOfView>(povJson);

        // 2. acting, by Command; the report goes back under the command's identifier.
        if (message.Ports.TryGetValue(_commandPort, out var commandJson) && !string.IsNullOrWhiteSpace(commandJson))
        {
            var command = MpaiJson.FromJson<UserCommand>(commandJson);
            var report = new CommandReport(command);
            Apply(command, pov, report);
            if (_reportOut.Length > 0) outputs[_reportOut] = MpaiJson.ToJson(report.Report());
        }
        else if (pov is not null && _open is not null)
            _open = _ase.SetBasicSceneListener(_open, pov).AssetId;

        // 3. the open Scene, as it now stands.
        if (_open is not null) outputs[_sceneOut] = MpaiJson.ToJson(_ase.MaterializeBasicScene(_open));
        return Task.FromResult(new Message
        {
            MessageId   = Guid.NewGuid().ToString(),
            MessageType = _open is null ? "NoOpenScene" : "BasicAudioSceneDescriptors",
            DataType    = _open is null ? "" : "OSD-BAS-V1.5",
            Ports       = outputs
        });
    }

    private void Apply(UserCommand command, PointOfView? pov, CommandReport report)
    {
        var data = command.UserCommandData;
        if (data is null) return;
        var listener = data.UserPoV ?? pov;

        if (data.ModifiedObjects is { Objects.Count: > 0 })
            report.NotSupported("ModifiedObjects", _open, "what an Object is like is Audio Object Editing's");
        if (data.TranslatedObjects is { Objects.Count: > 0 })
            report.NotSupported("TranslatedObjects", _open, "speech is translated by Speech Object Editing");

        if (data.AddedObjects is { Objects.Count: > 0 } added)
            foreach (var entry in added.Objects)
            {
                var id = IdOf(entry.ObjectID);
                if (id is null || !id.StartsWith("BAO", StringComparison.Ordinal))
                {
                    report.Failed("AddedObjects", id, "a Basic Audio Scene holds Basic Audio Objects, named by their identifiers");
                    continue;
                }
                try
                {
                    _open = _open is null
                        ? _ase.CreateBasicScene(id, listener ?? Origin(), Placement(entry.SpatialAttitude)).AssetId
                        : _ase.AddObjectToBasicScene(_open, id, Placement(entry.SpatialAttitude)).AssetId;
                    report.Done("AddedObjects", id, _open);
                }
                catch (Exception failure) { report.Failed("AddedObjects", id, failure.Message); }
            }

        if (_open is null)
        {
            foreach (var (action, any) in new[] { ("MovedObjects", data.MovedObjects is { Objects.Count: > 0 }),
                                                  ("ChangedObjects", data.ChangedObjects is { Objects.Count: > 0 }),
                                                  ("RemovedObjects", data.RemovedObjects is { Objects.Count: > 0 }) })
                if (any) report.Failed(action, null, "no Basic Audio Scene is open");
            return;
        }

        if (data.MovedObjects is { Objects.Count: > 0 } moved)
            foreach (var entry in moved.Objects)
                Place("MovedObjects", IdOf(entry.ObjectID), entry.NewSpatialAttitude, report);

        if (data.ChangedObjects is { Objects.Count: > 0 } changed)
            foreach (var entry in changed.Objects)
                Place("ChangedObjects", IdOf(entry.ObjectID), entry.SpatialAttitude, report);

        if (data.RemovedObjects is { Objects.Count: > 0 } removed)
            foreach (var entry in removed.Objects)
            {
                var id = IdOf(entry.ObjectID);
                var result = id is null ? null : _ase.RemoveFromBasicScene(_open, id);
                if (result is null) report.Failed("RemovedObjects", id, "the open Scene has no such member");
                else { _open = result.AssetId; report.Done("RemovedObjects", id, _open); }
            }

        // Once, on the Scene: its User PoV overrides each member's.
        if (listener is not null)
        {
            var before = _open;
            _open = _ase.SetBasicSceneListener(_open, listener).AssetId;
            report.Done("UserPoV", before, _open);
        }
    }

    // A move or a change is the member re-placed at the new attitude.
    private void Place(string action, string? id, SpatialAttitude? attitude, CommandReport report)
    {
        var result = id is null ? null : _ase.ReplaceInBasicScene(_open!, id, Placement(attitude));
        if (result is null) { report.Failed(action, id, "the open Scene has no such member"); return; }
        _open = result.AssetId;
        report.Done(action, id, _open);
    }

    private static PointOfView Origin() => new() { PointOfViewID = Guid.NewGuid().ToString(), CartPosition = [0, 0, 0], Orientation = [0, 0, 0] };

    private static SpaceTime? Placement(SpatialAttitude? attitude) =>
        attitude is null ? null : new SpaceTime { SpatialAttitude1 = attitude };

    private static string? IdOf(ManagedObject? managed) =>
        managed is null ? null
        : !string.IsNullOrWhiteSpace(managed.ObjectID) ? managed.ObjectID
        : !string.IsNullOrWhiteSpace(managed.BasicAudioObject?.BasicAudioObjectID) ? managed.BasicAudioObject!.BasicAudioObjectID
        : null;
}
