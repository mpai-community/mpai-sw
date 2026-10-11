using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AIF.Controller;
using AIF.SharedStorage;

using Mpai.Cae.Soe;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Cae.Sse;

// CAE-SSE-V1.0 - Speech Scene Editing, as the author's Reference Model of Audio Scene
// Management draws it (CAE Reference Models, 2026/10/04): a Basic Speech Object (from
// Speech Object Editing), a Basic Speech Scene, a User Command and a User PoV in; the
// Basic Speech Scene as edited, and the User Command with its report, out.
//
// OPEN BY PORT, ACT BY COMMAND, as Audio Scene Editing does. A Basic Speech Scene
// arriving is opened (kept, when Shared Storage does not hold it yet). A User Command
// acts on the open Scene - the first Object added to no open Scene creates one:
//
//   AddedObjects     place the named Basic Speech Objects
//   MovedObjects     old attitude -> new attitude
//   ChangedObjects   where a placed Object is
//   RemovedObjects   take placed Objects out
//   UserPoV          where the Scene is heard from (also on its own Port)
//
// Every edit is a new version in Shared Storage (BSS000001 ...). A member is held by
// its identifier and read from Shared Storage when the Scene is output, so a Speech
// Object translated in its place is heard translated in every Scene holding it.
public sealed class SseAimProcessor : IAimProcessor
{
    private readonly ISharedStorage _storage;
    private readonly string _mInstanceId;

    private readonly string _commandPort;
    private readonly string _objectPort;
    private readonly string _scenePort;
    private readonly string _povPort;
    private readonly string _sceneOut;
    private readonly string _reportOut;

    private string? _open;

    public string InstanceId { get; }

    public SseAimProcessor(string instanceId, ISharedStorage storage, AimPortReader ports, string mInstanceId = "ASM")
    {
        InstanceId   = instanceId;
        _storage     = storage;
        _mInstanceId = mInstanceId;
        _commandPort = ports.Input("CAE-UCM-V1.0");
        _objectPort  = ports.InputOrDefault("OSD-BSO-V1.5", "");
        _scenePort   = ports.Input("OSD-BSS-V1.5");
        _povPort     = ports.InputOrDefault("OSD-OPV-V1.5", "");
        _sceneOut    = ports.Output("OSD-BSS-V1.5");
        _reportOut   = ports.OutputOrDefault("CAE-UCM-V1.0", "");
    }

    public Task<Message> ProcessAsync(Message message)
    {
        var outputs = new Dictionary<string, string>();
        string? In(string port) =>
            port.Length > 0 && message.Ports.TryGetValue(port, out var json) && !string.IsNullOrWhiteSpace(json) ? json : null;

        // A Speech Object from Speech Object Editing is in Shared Storage already; one
        // given otherwise is kept, so that a command can name it.
        if (In(_objectPort) is { } objectJson)
        {
            var speech = MpaiJson.FromJson<BasicSpeechObject>(objectJson);
            if (SpeechStore.Get(_storage, speech.BasicSpeechObjectID) is null) SpeechStore.Keep(_storage, speech);
        }

        // 1. opening, by Port.
        if (In(_scenePort) is { } sceneJson)
        {
            var scene = MpaiJson.FromJson<BasicSpeechSceneDescriptors>(sceneJson);
            _open = !string.IsNullOrWhiteSpace(scene.BasicSpeechSceneDescriptorsID) && _storage.MPAI_AIFM_SharedStorage_Exists(scene.BasicSpeechSceneDescriptorsID)
                ? scene.BasicSpeechSceneDescriptorsID
                : Save(scene.BasicSpeechSceneItems, scene.UserPoV, scene);
            Console.WriteLine($"[CAE-SSE-V1.0] opened {_open}");
        }

        PointOfView? pov = In(_povPort) is { } povJson ? MpaiJson.FromJson<PointOfView>(povJson) : null;

        // 2. acting, by Command; the report goes back under the command's identifier.
        if (In(_commandPort) is { } commandJson)
        {
            var command = MpaiJson.FromJson<UserCommand>(commandJson);
            var report = new CommandReport(command);
            Apply(command, pov, report);
            if (_reportOut.Length > 0) outputs[_reportOut] = MpaiJson.ToJson(report.Report());
        }
        else if (pov is not null && Load(_open) is { } scene)
            _open = Save(scene.BasicSpeechSceneItems, pov, scene);

        // 3. the open Scene, as it now stands: each member read from Shared Storage.
        if (Load(_open) is { } open) outputs[_sceneOut] = MpaiJson.ToJson(Materialized(open));
        return Task.FromResult(new Message
        {
            MessageId   = Guid.NewGuid().ToString(),
            MessageType = _open is null ? "NoOpenScene" : "BasicSpeechSceneDescriptors",
            DataType    = _open is null ? "" : "OSD-BSS-V1.5",
            Ports       = outputs
        });
    }

    private void Apply(UserCommand command, PointOfView? pov, CommandReport report)
    {
        var data = command.UserCommandData;
        if (data is null) return;
        var listener = data.UserPoV ?? pov;

        if (data.ModifiedObjects is { Objects.Count: > 0 })
            report.NotSupported("ModifiedObjects", _open, "what an Object is like is Speech Object Editing's");
        if (data.TranslatedObjects is { Objects.Count: > 0 })
            report.NotSupported("TranslatedObjects", _open, "speech is translated by Speech Object Editing");

        var scene = Load(_open);
        var items = scene?.BasicSpeechSceneItems.ToList() ?? new List<BasicSpeechSceneItem>();
        var before = _open;
        var changed = false;

        if (data.AddedObjects is { Objects.Count: > 0 } added)
            foreach (var entry in added.Objects)
            {
                var id = IdOf(entry.ObjectID);
                if (id is null || SpeechStore.Get(_storage, id) is null) { report.Failed("AddedObjects", id, "no such Basic Speech Object"); continue; }
                items.Add(new BasicSpeechSceneItem { ObjectSpaceTime = Placement(entry.SpatialAttitude), SpeechObject = new BasicSpeechObject { BasicSpeechObjectID = id } });
                report.Done("AddedObjects", id); changed = true;
            }

        void Replace(string action, ManagedObject? named, Func<BasicSpeechSceneItem, BasicSpeechSceneItem?> change)
        {
            var id = IdOf(named);
            var at = id is null ? -1 : items.FindIndex(i => i.SpeechObject?.BasicSpeechObjectID == id);
            if (at < 0) { report.Failed(action, id, scene is null && !changed ? "no Basic Speech Scene is open" : "the open Scene has no such member"); return; }
            if (change(items[at]) is { } kept) items[at] = kept; else items.RemoveAt(at);
            report.Done(action, id); changed = true;
        }
        BasicSpeechSceneItem At(BasicSpeechSceneItem i, SpatialAttitude? a) =>
            new() { SpeechObject = i.SpeechObject, UserPoV = i.UserPoV, ObjectSpaceTime = Placement(a) ?? i.ObjectSpaceTime };

        if (data.MovedObjects is { Objects.Count: > 0 } moved)
            foreach (var entry in moved.Objects) Replace("MovedObjects", entry.ObjectID, i => At(i, entry.NewSpatialAttitude));
        if (data.ChangedObjects is { Objects.Count: > 0 } changes)
            foreach (var entry in changes.Objects) Replace("ChangedObjects", entry.ObjectID, i => At(i, entry.SpatialAttitude));
        if (data.RemovedObjects is { Objects.Count: > 0 } removed)
            foreach (var entry in removed.Objects) Replace("RemovedObjects", entry.ObjectID, _ => null);

        if (listener is not null)
        {
            if (scene is null && !changed) report.Failed("UserPoV", null, "no Basic Speech Scene is open");
            else { report.Done("UserPoV", before); changed = true; }
        }

        if (!changed) return;
        _open = Save(items, listener ?? scene?.UserPoV ?? Origin(), scene);
        report.Produced(_open);   // every action done made this version
    }

    // A new version: BSS000001, BSS000002 ...
    private string Save(IEnumerable<BasicSpeechSceneItem> items, PointOfView? userPoV, BasicSpeechSceneDescriptors? from)
    {
        const string counter = "_counter:BSS";
        long next = _storage.MPAI_AIFM_SharedStorage_Exists(counter)
            ? long.Parse(Encoding.UTF8.GetString(_storage.MPAI_AIFM_SharedStorage_Get(counter))) + 1 : 1;
        _storage.MPAI_AIFM_SharedStorage_Put(counter, Encoding.UTF8.GetBytes(next.ToString()));
        var id = $"BSS{next:D6}";
        // A member is kept by its identifier only; its Speech Object stays where it is.
        var kept = items.Select(i => new BasicSpeechSceneItem
        {
            ObjectSpaceTime = i.ObjectSpaceTime, UserPoV = i.UserPoV,
            SpeechObject = i.SpeechObject is { } s ? new BasicSpeechObject { BasicSpeechObjectID = SpeechStore.Get(_storage, s.BasicSpeechObjectID) is null ? SpeechStore.Keep(_storage, s) : s.BasicSpeechObjectID } : null
        }).ToList();
        var scene = new BasicSpeechSceneDescriptors
        {
            MInstanceID = from?.MInstanceID is { Length: > 0 } m ? m : _mInstanceId,
            UEnvironmentID = from?.UEnvironmentID,
            BasicSpeechSceneDescriptorsID = id,
            BasicSpeechSceneDescriptorsTime = from?.BasicSpeechSceneDescriptorsTime ?? SimpleTime.At(DateTimeOffset.UtcNow),
            BasicSpeechSceneDescriptorsSpaceTime = from?.BasicSpeechSceneDescriptorsSpaceTime ?? new SpaceTime(),
            UserPoV = userPoV, ClosedSpace = from?.ClosedSpace, AcousticProfile = from?.AcousticProfile,
            ObjectCount = kept.Count, BasicSpeechSceneItems = kept
        };
        _storage.MPAI_AIFM_SharedStorage_Put(id, Encoding.UTF8.GetBytes(MpaiJson.ToJson(scene)));
        return id;
    }

    private BasicSpeechSceneDescriptors? Load(string? id) =>
        id is null || !_storage.MPAI_AIFM_SharedStorage_Exists(id) ? null
        : MpaiJson.FromJson<BasicSpeechSceneDescriptors>(Encoding.UTF8.GetString(_storage.MPAI_AIFM_SharedStorage_Get(id)));

    // The Scene with each member's Speech Object as Shared Storage now holds it.
    private BasicSpeechSceneDescriptors Materialized(BasicSpeechSceneDescriptors scene) => new()
    {
        MInstanceID = scene.MInstanceID, UEnvironmentID = scene.UEnvironmentID,
        BasicSpeechSceneDescriptorsID = scene.BasicSpeechSceneDescriptorsID, BasicSpeechSceneDescriptorsTime = scene.BasicSpeechSceneDescriptorsTime,
        BasicSpeechSceneDescriptorsSpaceTime = scene.BasicSpeechSceneDescriptorsSpaceTime, UserPoV = scene.UserPoV,
        ClosedSpace = scene.ClosedSpace, AcousticProfile = scene.AcousticProfile, ObjectCount = scene.ObjectCount,
        BasicSpeechSceneItems = scene.BasicSpeechSceneItems.Select(i => new BasicSpeechSceneItem
        {
            ObjectSpaceTime = i.ObjectSpaceTime, UserPoV = i.UserPoV,
            SpeechObject = i.SpeechObject is { } s ? SpeechStore.Get(_storage, s.BasicSpeechObjectID) ?? s : null
        }).ToList(),
        DataXMData = scene.DataXMData, DescrMetadata = scene.DescrMetadata
    };

    private static PointOfView Origin() => new() { PointOfViewID = Guid.NewGuid().ToString(), CartPosition = [0, 0, 0], Orientation = [0, 0, 0] };

    private static SpaceTime? Placement(SpatialAttitude? attitude) =>
        attitude is null ? null : new SpaceTime { SpatialAttitude1 = attitude };

    private static string? IdOf(ManagedObject? managed) =>
        managed is null ? null
        : !string.IsNullOrWhiteSpace(managed.ObjectID) ? managed.ObjectID
        : !string.IsNullOrWhiteSpace(managed.SpeechObject?.BasicSpeechObjectID) ? managed.SpeechObject!.BasicSpeechObjectID
        : null;
}
