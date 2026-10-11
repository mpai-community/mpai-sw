using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;
using AIF.SharedStorage;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Cae.Soe;

// CAE-SOE-V1.0 - Speech Object Editing, as the author's Reference Model of Audio
// Scene Management draws it (CAE Reference Models, 2026/10/04): User Command, User
// PoV and a Basic Speech Object in; the Basic Speech Object as edited, and the User
// Command with its report, out. The Object goes on to Speech Scene Editing.
//
// OPEN BY PORT, ACT BY COMMAND, as Audio Object Editing does. A Basic Speech Object
// arriving is opened when Shared Storage holds it, and kept when it is new. Then a
// User Command acts on what is open:
//
//   UserPoV            where it is heard from, when heard on its own
//   ChangedObjects     its Qualifier (a Speech Qualifier)
//   TranslatedObjects  translated by Text and Speech Translation (MMC-TST): the Speech
//                      and a Language Selector go out; the Translation, when it comes
//                      back, replaces the original under its identifier (the author,
//                      2026/10/03) - so every Speech Scene holding it holds the
//                      translation, at its place.
//
// A Speech Object is kept under its own identifier, changed in place.
public sealed class SoeAimProcessor : IAimProcessor
{
    private readonly ISharedStorage _storage;

    private readonly string _commandPort;
    private readonly string _povPort;
    private readonly string _objectPort;        // OSD-BSO #1
    private readonly string _translationPort;   // OSD-BSO #2, from Text and Speech Translation
    private readonly string _objectOut;         // OSD-BSO #1
    private readonly string _speechOut;         // OSD-BSO #2, to Text and Speech Translation
    private readonly string _languageOut;       // OSD-SEL
    private readonly string _reportOut;

    private string? _open;

    // The translation asked for and not yet arrived, and the report that waits for it:
    // in continuous execution the command and the translation come in different runs.
    private (ObjectTranslation Entry, UserCommand Command)? _pending;

    public string InstanceId { get; }

    public SoeAimProcessor(string instanceId, ISharedStorage storage, AimPortReader ports)
    {
        InstanceId       = instanceId;
        _storage         = storage;
        _commandPort     = ports.Input("CAE-UCM-V1.0");
        _povPort         = ports.InputOrDefault("OSD-OPV-V1.5", "");
        _objectPort      = ports.Input("OSD-BSO-V1.5", 1);
        _translationPort = ports.InputOrDefault("OSD-BSO-V1.5", 2, "");
        _objectOut       = ports.Output("OSD-BSO-V1.5", 1);
        _speechOut       = ports.OutputOrDefault("OSD-BSO-V1.5", 2, "");
        _languageOut     = ports.OutputOrDefault("OSD-SEL-V1.5", "");
        _reportOut       = ports.OutputOrDefault("CAE-UCM-V1.0", "");
    }

    public Task<Message> ProcessAsync(Message message)
    {
        var outputs = new Dictionary<string, string>();
        string? In(string port) =>
            port.Length > 0 && message.Ports.TryGetValue(port, out var json) && !string.IsNullOrWhiteSpace(json) ? json : null;

        // 1. opening, or keeping, by Port.
        if (In(_objectPort) is { } objectJson)
        {
            var speech = MpaiJson.FromJson<BasicSpeechObject>(objectJson);
            _open = SpeechStore.Get(_storage, speech.BasicSpeechObjectID) is not null ? speech.BasicSpeechObjectID : SpeechStore.Keep(_storage, speech);
            Console.WriteLine($"[CAE-SOE-V1.0] open {_open}");
        }

        // 2. a User PoV on its Port.
        if (In(_povPort) is { } povJson && Open() is { } heard)
            SpeechStore.Put(_storage, SpeechStore.With(heard, userPoV: MpaiJson.FromJson<PointOfView>(povJson)));

        // 3. a translation come back: it replaces its original, and the command is reported.
        if (In(_translationPort) is { } translationJson && _pending is { } pending)
        {
            _pending = null;
            var report = new CommandReport(pending.Command);
            var id = IdOf(pending.Entry.ObjectID);
            if (id is not null && SpeechStore.Get(_storage, id) is { } original)
            {
                SpeechStore.Put(_storage, SpeechStore.Replacing(original, MpaiJson.FromJson<BasicSpeechObject>(translationJson), pending.Entry.SpeechQualifier));
                _open = id;
                report.Done("TranslatedObjects", id, id);
                Console.WriteLine($"[CAE-SOE-V1.0] {id} replaced by its translation into {pending.Entry.TargetLanguage}");
            }
            else report.Failed("TranslatedObjects", id, "no such Speech Object to replace");
            if (_reportOut.Length > 0) outputs[_reportOut] = MpaiJson.ToJson(report.Report());
        }

        // 4. acting, by Command.
        if (In(_commandPort) is { } commandJson)
        {
            var command = MpaiJson.FromJson<UserCommand>(commandJson);
            var report = new CommandReport(command);
            var waiting = Apply(command, report, outputs);
            // A translation is reported when it comes back; anything else now.
            if (!waiting && _reportOut.Length > 0) outputs[_reportOut] = MpaiJson.ToJson(report.Report());
        }

        // 5. the open Object, as it now stands.
        if (Open() is { } open) outputs[_objectOut] = MpaiJson.ToJson(open);
        return Task.FromResult(new Message
        {
            MessageId   = Guid.NewGuid().ToString(),
            MessageType = _open is null ? "NoOpenObject" : "BasicSpeechObject",
            DataType    = _open is null ? "" : "OSD-BSO-V1.5",
            Ports       = outputs
        });
    }

    private const string SpeechQualifierHeader = "TFA-SPQ-V1.5";

    private BasicSpeechObject? Open() => _open is null ? null : SpeechStore.Get(_storage, _open);

    // True when the command waits for a translation, which then reports it.
    private bool Apply(UserCommand command, CommandReport report, Dictionary<string, string> outputs)
    {
        var data = command.UserCommandData;
        if (data is null) return false;
        const string inScene = "where an Object is in a Scene is Speech Scene Editing's";
        foreach (var (action, any) in new[] { ("AddedObjects", data.AddedObjects is { Objects.Count: > 0 }),
                                              ("MovedObjects", data.MovedObjects is { Objects.Count: > 0 }),
                                              ("RemovedObjects", data.RemovedObjects is { Objects.Count: > 0 }) })
            if (any) report.NotSupported(action, _open, inScene);
        if (data.ModifiedObjects is { Objects.Count: > 0 })
            report.NotSupported("ModifiedObjects", _open, "a Speech Object has no Acoustic Profile to modify");

        if (data.UserPoV is not null)
        {
            if (Open() is { } speech) { SpeechStore.Put(_storage, SpeechStore.With(speech, userPoV: data.UserPoV)); report.Done("UserPoV", _open, _open); }
            else report.Failed("UserPoV", null, "no Basic Speech Object is open");
        }

        if (data.ChangedObjects is { Objects.Count: > 0 } changed)
            foreach (var entry in changed.Objects)
            {
                var id = IdOf(entry.ObjectID) ?? _open;
                var speech = id is null ? null : SpeechStore.Get(_storage, id);
                if (speech is null) { report.Failed("ChangedObjects", id, "no such Basic Speech Object"); continue; }
                SpeechQualifier? qualifier = null;
                if (entry.Qualifier is { } q && System.Text.Json.JsonSerializer.Serialize(q) is var qJson &&
                    System.Text.Json.Nodes.JsonNode.Parse(qJson)?["Header"]?.GetValue<string>() == SpeechQualifierHeader)
                    qualifier = MpaiJson.FromJson<SpeechQualifier>(qJson);
                if (qualifier is null && entry.SpatialAttitude is null) { report.Failed("ChangedObjects", id, "nothing to change: a Speech Qualifier or a Spatial Attitude is expected"); continue; }
                SpeechStore.Put(_storage, SpeechStore.With(speech, qualifier: qualifier,
                    spaceTime: entry.SpatialAttitude is null ? null : new SpaceTime { SpatialAttitude1 = entry.SpatialAttitude }));
                report.Done("ChangedObjects", id, id);
            }

        if (data.TranslatedObjects is { Objects.Count: > 0 } translated)
        {
            var entry = translated.Objects[0];
            var id = IdOf(entry.ObjectID);
            var speech = id is null ? null : SpeechStore.Get(_storage, id);
            if (_speechOut.Length == 0 || _languageOut.Length == 0) report.Failed("TranslatedObjects", id, "this AIM has no Port to Text and Speech Translation");
            else if (speech is null) report.Failed("TranslatedObjects", id, "no such Basic Speech Object");
            else if (entry.TargetLanguage is not { Length: > 0 } target) report.Failed("TranslatedObjects", id, "no target language");
            else
            {
                var source = speech.SpeechQualifier?.Attributes?.Metadata?.Language?.LanguageCode;
                outputs[_speechOut]   = MpaiJson.ToJson(speech);
                outputs[_languageOut] = MpaiJson.ToJson(BasicSelectorObject.Languages(source, target));
                Console.WriteLine($"[CAE-SOE-V1.0] translating {id} from {source ?? "its language"} into {target}");
                for (var i = 1; i < translated.Objects.Count; i++)
                    report.NotSupported("TranslatedObjects", IdOf(translated.Objects[i].ObjectID), "one Speech Object at a time");
                _pending = (entry, command);
                return true;
            }
        }
        return false;
    }

    private static string? IdOf(ManagedObject? managed) =>
        managed is null ? null
        : !string.IsNullOrWhiteSpace(managed.ObjectID) ? managed.ObjectID
        : !string.IsNullOrWhiteSpace(managed.SpeechObject?.BasicSpeechObjectID) ? managed.SpeechObject!.BasicSpeechObjectID
        : null;
}
