using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

// AudioObject and the other OSD audio schema types live in Mpai.Core.OSD,
// a separate namespace within the same assembly - as AoeAim's own using list
// shows. Being in the same project is not the same as being in the same
// namespace, which is what the first version of this file assumed.
using Mpai.Core.OSD;

namespace Mpai.Core;

// CAE-UCM-V1.0 - User Command.
// CAE3/V1.0/data/UserCommand.json
//
// WHICH FIELD IS POPULATED IS THE OPERATION. There is no operation name and no
// enumeration to keep in step with the AIMs: a Command that carries
// AddedObjects is an add, one that carries MovedObjects is a move. An AIM
// dispatches on presence, and a Command carrying nothing it recognises is one
// meant for a different AIM.
//
// NO COMMAND NAMES ITS TARGET. CAE-AOE edits one object at a time and CAE-ASE
// one scene at a time; a Command acts on whatever is open. Opening is not a
// Command - it is data arriving at an input Port, which is how an AIM is told
// anything.
public sealed class UserCommand
{
    public string Header { get; init; } = "CAE-UCM-V1.0";

    public string? MInstanceID    { get; init; }
    public string? UEnvironmentID { get; init; }

    public string      UserCommandID   { get; init; } = string.Empty;
    public SimpleTime? UserCommandTime { get; init; }

    public UserCommandData? UserCommandData { get; init; }

    // THE OUTPUT HALF (the author, 2026/10/04: "User Command schema should have one
    // half for input and one for Output (report)"): how the AIM that executed the
    // command did, under the command's own UserCommandID.
    public UserCommandReport? UserCommandReport { get; init; }

    public string? DescrMetadata { get; init; }
}

public sealed class UserCommandReport
{
    public SimpleTime?        ReportTime { get; init; }
    public string             Outcome    { get; init; } = "Done";   // Done | Partly | Failed | Ignored
    public List<ReportAction> Actions    { get; init; } = new();
}

public sealed class ReportAction
{
    public string  Action   { get; init; } = "";       // the UserCommandData field it comes from
    public string? ObjectID { get; init; }
    public string  Outcome  { get; init; } = "Done";   // Done | Failed | NotSupported
    public string? ResultID { get; set; }
    public string? Reason   { get; init; }
}

// WHAT AN AIM SAYS IT DID with one User Command: each action as it is executed, then
// the report - the command's identifier and data, and its report half.
public sealed class CommandReport(UserCommand command)
{
    private readonly List<ReportAction> actions = new();

    public void Done(string action, string? objectId, string? resultId = null) =>
        actions.Add(new ReportAction { Action = action, ObjectID = objectId, ResultID = resultId });

    public void Failed(string action, string? objectId, string reason) =>
        actions.Add(new ReportAction { Action = action, ObjectID = objectId, Outcome = "Failed", Reason = reason });

    public void NotSupported(string action, string? objectId, string reason) =>
        actions.Add(new ReportAction { Action = action, ObjectID = objectId, Outcome = "NotSupported", Reason = reason });

    // The version the actions done so far produced, for those that do not name one yet.
    public void Produced(string resultId)
    {
        foreach (var a in actions) if (a.Outcome == "Done" && a.ResultID is null) a.ResultID = resultId;
    }

    public bool Any => actions.Count > 0;

    public UserCommand Report() => new()
    {
        MInstanceID = command.MInstanceID, UEnvironmentID = command.UEnvironmentID,
        UserCommandID = command.UserCommandID, UserCommandTime = command.UserCommandTime ?? SimpleTime.At(DateTimeOffset.UtcNow),
        UserCommandData = command.UserCommandData,
        UserCommandReport = new UserCommandReport
        {
            ReportTime = SimpleTime.At(DateTimeOffset.UtcNow),
            Outcome = actions.Count == 0 ? "Ignored"
                    : actions.All(a => a.Outcome == "Done") ? "Done"
                    : actions.Any(a => a.Outcome == "Done") ? "Partly" : "Failed",
            Actions = actions.ToList()
        }
    };
}

public sealed class UserCommandData
{
    // Qualify an operation rather than being one.
    public PointOfView? UserPoV { get; init; }
    public double?      LUFS    { get; init; }

    // The eight operations. Exactly one is expected to be populated.
    public ManagedObject?   AcquiredObject  { get; init; }
    public ManagedObject?   DeliveredObject { get; init; }

    public ObjectPlacements? AddedObjects    { get; init; }
    public ObjectPlacements? RemovedObjects  { get; init; }
    public ObjectMovements?  MovedObjects    { get; init; }
    public ObjectChanges?    ChangedObjects  { get; init; }
    public ObjectChanges?    ModifiedObjects { get; init; }

    // Speech Objects to translate (the author, 2026/10/03), with Text and Speech
    // Translation; each translation replaces its original, at its place.
    public ObjectTranslations? TranslatedObjects { get; init; }
}

// An identifier or the object itself: OSD ObjectOrID. Carrying only the
// identifier is the usual case, and the AIM fetches the content from Shared
// Storage. In JSON it is what the schema says - the Object, or its ID as a
// string - and the converter below reads and writes it so.
[JsonConverter(typeof(ManagedObjectConverter))]
public sealed class ManagedObject
{
    public string? ObjectID { get; init; }

    public AudioObject?       AudioObject       { get; init; }
    public BasicAudioObject?  BasicAudioObject  { get; init; }
    public BasicSpeechObject? SpeechObject      { get; init; }
    public BasicVisualObject? VisualObject      { get; init; }
}

public sealed class ObjectPlacements
{
    public List<ObjectPlacement> Objects { get; init; } = new();
}

public sealed class ObjectPlacement
{
    public ManagedObject?    ObjectID        { get; init; }
    public SpatialAttitude?  SpatialAttitude { get; init; }
}

public sealed class ObjectMovements
{
    public List<ObjectMovement> Objects { get; init; } = new();
}

public sealed class ObjectMovement
{
    public ManagedObject?   ObjectID           { get; init; }
    public AcousticProfile? AcousticProfile    { get; init; }
    public SpatialAttitude? OldSpatialAttitude { get; init; }
    public SpatialAttitude? NewSpatialAttitude { get; init; }
}

// Serves both ChangedObjects and ModifiedObjects. The distinction is which
// attributes an operation touches, not the shape of the entry:
//
//   Changed  - EXTERNAL attributes: where the object is, how it is moving.
//   Modified - INTERNAL attributes: what the object itself is like.
//
// The two carry the same fields today because the schema declares them so; a
// reader cannot tell them apart from the names alone, which is worth
// remembering when reading a Command.
// Speech Objects and the Speech Qualifier of each one's translation: its
// Attributes.Metadata.Language states the target language. The source language
// is the one the Speech Object's own Qualifier states.
public sealed class ObjectTranslations
{
    public List<ObjectTranslation> Objects { get; init; } = new();
}

public sealed class ObjectTranslation
{
    public ManagedObject?   ObjectID        { get; init; }
    public SpeechQualifier? SpeechQualifier { get; init; }

    [JsonIgnore]
    public string? TargetLanguage => SpeechQualifier?.Attributes?.Metadata?.Language?.LanguageCode;
}

public sealed class ObjectChanges
{
    public List<ObjectChange> Objects { get; init; } = new();
}

public sealed class ObjectChange
{
    public ManagedObject?   ObjectID        { get; init; }
    public SpatialAttitude? SpatialAttitude { get; init; }
    public AcousticProfile? AcousticProfile { get; init; }

    // Present on ChangedObjects in the schema; describes the object's own
    // nature rather than its placement.
    public object? Qualifier { get; init; }
}

public sealed class ManagedObjectConverter : JsonConverter<ManagedObject>
{
    public override ManagedObject? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return new ManagedObject { ObjectID = reader.GetString() };
        var node = JsonNode.Parse(ref reader);
        var json = node?.ToJsonString() ?? "{}";
        return node?["Header"]?.GetValue<string>() switch
        {
            "OSD-AUO-V1.5" => Of(JsonSerializer.Deserialize<AudioObject>(json, options), o => o.AudioObjectID, o => new() { AudioObject = o }),
            "OSD-BAO-V1.5" => Of(JsonSerializer.Deserialize<BasicAudioObject>(json, options), o => o.BasicAudioObjectID, o => new() { BasicAudioObject = o }),
            "OSD-BSO-V1.5" => Of(JsonSerializer.Deserialize<BasicSpeechObject>(json, options), o => o.BasicSpeechObjectID, o => new() { SpeechObject = o }),
            "OSD-BVO-V1.5" => Of(JsonSerializer.Deserialize<BasicVisualObject>(json, options), o => o.BasicVisualObjectID, o => new() { VisualObject = o }),
            _ => new ManagedObject { ObjectID = node?["ObjectID"]?.GetValue<string>() }
        };
    }

    private static ManagedObject Of<T>(T? value, Func<T, string> id, Func<T, ManagedObject> make) where T : class =>
        value is null ? new ManagedObject() : Merge(make(value), id(value));

    private static ManagedObject Merge(ManagedObject m, string id) => new()
    {
        ObjectID = id, AudioObject = m.AudioObject, BasicAudioObject = m.BasicAudioObject, SpeechObject = m.SpeechObject, VisualObject = m.VisualObject
    };

    public override void Write(Utf8JsonWriter writer, ManagedObject value, JsonSerializerOptions options)
    {
        object? whole = (object?)value.AudioObject ?? (object?)value.BasicAudioObject ?? (object?)value.SpeechObject ?? value.VisualObject;
        if (whole is null) writer.WriteStringValue(value.ObjectID ?? "");
        else JsonSerializer.Serialize(writer, whole, whole.GetType(), options);
    }
}

// MMC-TFU-V1.5 - Text For UA (MMC/V2.5/data/TextForUA.json): what a dialogue AIM
// addresses to the User Agent, not to the User - an action only the User Agent
// performs (in CAE-ASM: undo, save, stop), in the App's vocabulary.
public sealed class TextForUA
{
    public string Header { get; init; } = "MMC-TFU-V1.5";
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string TextForUAID { get; init; } = Guid.NewGuid().ToString();
    public SimpleTime? TextForUATime { get; init; }
    public string Text { get; init; } = "";
    public string? DescrMetadata { get; init; }
}
