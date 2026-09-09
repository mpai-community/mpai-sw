using System;
using System.Collections.Generic;

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

    public string? DescrMetadata { get; init; }
}

public sealed class UserCommandData
{
    // Qualify an operation rather than being one.
    public PointOfView? UserPoV { get; init; }
    public double?      LUFS    { get; init; }

    // The seven operations. Exactly one is expected to be populated.
    public ManagedObject?   AcquiredObject  { get; init; }
    public ManagedObject?   DeliveredObject { get; init; }

    public ObjectPlacements? AddedObjects    { get; init; }
    public ObjectPlacements? RemovedObjects  { get; init; }
    public ObjectMovements?  MovedObjects    { get; init; }
    public ObjectChanges?    ChangedObjects  { get; init; }
    public ObjectChanges?    ModifiedObjects { get; init; }
}

// An identifier or the object itself: OSD ObjectOrID. Carrying only the
// identifier is the usual case, and the AIM fetches the content from Shared
// Storage.
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