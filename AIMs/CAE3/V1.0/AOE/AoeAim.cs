using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using AIF.SharedStorage;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Repository;

namespace Mpai.Cae.Aoe;

// ---------------------------------------------------------------------------
//  CAE-AOE-V1.0 - Audio Object Editing.
//
//  Ported directly onto the proposed MPAI-AIF Shared Storage API
//  (Put/Get/Delete/List/Exists/GetKeyInfo) - no intermediate Repository
//  class or method vocabulary (CreateAsset/SaveAsset/GetAsset/
//  CreateReference are gone). An AssetId doubles as its own Shared Storage
//  key: its type prefix (BAO/AUO) already makes it self-describing for
//  List(prefix) queries, so no separate type-namespacing is layered on top
//  of it, per the Shared Storage proposal's Section 4.1 pattern.
//
//  Editing Principle preserved exactly: every edit produces a NEW key via a
//  fresh Put - never an overwrite of an existing AssetId's content. A
//  BasicAudioObject is the degenerate case of an AudioObject with no
//  sub-objects, not a separate type to special-case.
//
//  Cycle prevention in AddSubObject is a genuine new implementation here
//  (WouldCreateCycle below), not a port of the previous Repository's
//  CreateReference-based check, whose exact algorithm was not available at
//  port time - verified with its own dedicated test rather than assumed
//  equivalent.
// ---------------------------------------------------------------------------
public sealed class AoeAim
{
    private readonly ISharedStorage storage;

    // The M-Instance or U-Environment the Objects are made in: OSD requires the
    // first on an Audio Object. An application gives its own; ASM's own is "ASM".
    private readonly string mInstanceId;

    public AoeAim(ISharedStorage storage, string mInstanceId = "ASM")
    {
        this.storage = storage;
        this.mInstanceId = mInstanceId;
    }

    private static readonly JsonSerializerOptions DataOptions = new() { Converters = { new JsonStringEnumConverter() } };
    private static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, DataOptions);
    private static T Deserialize<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, DataOptions)!;

    // A fresh, sequential, type-prefixed key per Asset (AUO000001,
    // AUO000002, ...), matching the numbering scheme already familiar from
    // the whole project. The counter is itself a reserved key ("_counter:"
    // prefix, which List(typePrefix) never matches, since it doesn't start
    // with the type prefix), so it persists across sessions exactly like
    // everything else - proven by the same cross-session persistence
    // ISharedStorage itself already relies on.
    private string NextId(string typePrefix)
    {
        var counterKey = $"_counter:{typePrefix}";
        long next = storage.Exists(counterKey)
            ? long.Parse(Encoding.UTF8.GetString(storage.Get(counterKey))) + 1
            : 1;
        storage.Put(counterKey, Encoding.UTF8.GetBytes(next.ToString()));
        return $"{typePrefix}{next:D6}";
    }

    // Per Section 4.3 of the Shared Storage proposal - recorded for
    // completeness/future queries, even though AoeAim/AseAim's own public
    // surface does not currently expose GetReferences/GetReferrers.
    private void PutReference(string fromId, string toId)
    {
        storage.Put($"ref:{fromId}:{toId}", Array.Empty<byte>());
        storage.Put($"refby:{toId}:{fromId}", Array.Empty<byte>());
    }

    private static bool IsType(string assetId, string prefix) => assetId.StartsWith(prefix, StringComparison.Ordinal);

    // ID-stub entries: only the identifying field set, nothing else - a
    // minimal but genuinely valid instance of the "object" branch of the
    // schema's "object or id-string" oneOf. Materialize() resolves these
    // into full content; the stub itself is what's actually persisted.
    private static BasicAudioObject IdStubBao(string assetId) => new() { BasicAudioObjectID = assetId };

    // A Space/Time at the origin, for a composed Object that has not been placed.
    //
    // Position carries a CartPosition array, not X/Y/Z properties - which
    // ObjectsForm.BuildPlacementFrom has always shown, and this first version
    // invented X, Y and Z instead of reading it.
    private static SpaceTime AtTheOrigin() => new()
    {
        SpatialAttitude1 = new SpatialAttitude
        {
            ObjectSpatialAttitudeID = Guid.NewGuid().ToString(),
            Position = new Position
            {
                PositionID   = Guid.NewGuid().ToString(),
                CartPosition = new double[] { 0, 0, 0 }
            },

            // The composed Object's own frame, completely specified: at the
            // origin and unrotated. Everything it holds is placed relative to
            // this, which is why no component has to serve as the reference.
            //
            // Roll, pitch, yaw - the aerospace order, read as rotations about
            // X, Y and Z.
            Orientation = new Orientation
            {
                OrientationID = Guid.NewGuid().ToString(),
                EulerAngles   = new double[] { 0, 0, 0 }
            }
        }
    };
    private static AudioObject IdStubAuo(string assetId) => new() { AudioObjectID = assetId };

    // Stores a BasicAudioObject, and stops there.
    //
    // AN OBJECT HOLDING ONE OBJECT IS BASIC; one holding more than one is full.
    // The kind is a fact about the CONTENT, not a decision taken at creation, so
    // acquiring a file yields a BAO and nothing else. It becomes an AudioObject
    // when a second Object is composed into it - see AddSubObject - and would
    // become Basic again were it reduced to one.
    //
    // This used to mint an AUO immediately, wrapping the BAO as "the degenerate
    // case of an AudioObject with no sub-objects". That made every acquisition a
    // full Object of one, which is the case your rule says cannot exist.
    public RepositoryAsset CreateObject(BasicAudioObject bao)
    {
        var baoId = NextId("BAO");
        storage.Put(baoId, Serialize(bao.WithId(baoId)));

        return new RepositoryAsset { AssetId = baoId, AssetType = AssetType.BAO };
    }

    // Adds an existing Asset (a BAO or another AudioObject) as a child of an
    // existing AudioObject. Produces a NEW key for the AudioObject (per the
    // no-cascade Save rule: only the edited instance itself gets a new
    // identity; the child being added is untouched).
    // The PARENT may be a BAO, and that is the case which mints the first
    // AudioObject: open a Basic Object, add another, and the result is stored as
    // an AUO holding both. A BAO parent contributes itself as the first basic
    // entry; an AUO parent contributes the entries it already has.
    // childPlacement is where the child sits WITHIN the composed Object.
    //
    // The schema is explicit about the two levels: an entry's SpaceTime is
    // "where this Basic Audio Object is located within the containing Audio
    // Object. If absent, the Space/Time of the containing object applies." So a
    // child may be placed differently from its siblings - four voices standing
    // apart make a choir - and a child with no placement of its own simply sits
    // where the container sits.
    //
    // Passing null is therefore not a gap: it is the schema's own default.
    public RepositoryAsset AddSubObject(
        string audioObjectAssetId,
        string childAssetId,
        SpaceTime? childPlacement = null)
    {
        if (!storage.Exists(audioObjectAssetId))
            throw new InvalidOperationException($"{audioObjectAssetId} does not exist.");

        var parentIsBao = IsType(audioObjectAssetId, "BAO");
        var parentIsAuo = IsType(audioObjectAssetId, "AUO");
        if (!parentIsBao && !parentIsAuo)
            throw new InvalidOperationException($"{audioObjectAssetId} is not a BAO or an AudioObject.");

        if (!storage.Exists(childAssetId))
            throw new InvalidOperationException($"{childAssetId} does not exist.");

        var childIsBao = IsType(childAssetId, "BAO");
        var childIsAuo = IsType(childAssetId, "AUO");
        if (!childIsBao && !childIsAuo)
            throw new InvalidOperationException($"{childAssetId} is not a BAO or AudioObject.");

        if (childIsAuo && parentIsAuo && WouldCreateCycle(audioObjectAssetId, childAssetId))
            throw new InvalidOperationException($"Adding {childAssetId} to {audioObjectAssetId} would create a cycle.");

        var existingData = parentIsAuo
            ? Deserialize<AudioObject>(storage.Get(audioObjectAssetId))
            : null;

        var newId = NextId("AUO");
        PutReference(newId, childAssetId);

        var basicEntries = (existingData?.BasicAudioObjects ?? new List<BasicAudioObjectEntry>()).ToList();
        var subEntries   = (existingData?.SubAudioObjects  ?? new List<SubAudioObjectEntry>()).ToList();

        // A Basic parent joins its own composition as the first entry, and is
        // referenced by the new AudioObject like any other child.
        if (parentIsBao)
        {
            basicEntries.Add(new BasicAudioObjectEntry { BAObjectIDOrBAObject = IdStubBao(audioObjectAssetId) });
            PutReference(newId, audioObjectAssetId);
        }

        // The composed Object's OWN Space/Time. AudioObjectSpaceTime is
        // REQUIRED by the schema, and nothing set it: an Object minted from a
        // Basic parent carried null and did not satisfy its own definition. It
        // starts at the origin, which is where a newly composed Object is until
        // someone places it.
        var containerPlacement = existingData?.AudioObjectSpaceTime ?? AtTheOrigin();

        if (childIsBao)
        {
            basicEntries.Add(new BasicAudioObjectEntry
            {
                BasicAudioObjectSpaceTime = childPlacement,
                BAObjectIDOrBAObject      = IdStubBao(childAssetId)
            });
        }
        else
        {
            subEntries.Add(new SubAudioObjectEntry
            {
                SubAudioObjectSpaceTime  = childPlacement,
                SubAObjectIDOrSubAObject = IdStubAuo(childAssetId)
            });
        }

        storage.Put(newId, Serialize(new AudioObject
        {
            MInstanceID = mInstanceId,
            AudioObjectID = newId,
            AudioObjectTime = existingData?.AudioObjectTime,
            AudioObjectSpaceTime = containerPlacement,
            ParentAudioObjectIDs = existingData?.ParentAudioObjectIDs,
            BasicAudioObjectCount = basicEntries.Count,
            BasicAudioObjects = basicEntries.Count > 0 ? basicEntries : null,
            SubAudioObjectCount = subEntries.Count,
            SubAudioObjects = subEntries.Count > 0 ? subEntries : null
        }));

        return new RepositoryAsset { AssetId = newId, AssetType = AssetType.AUO };
    }

    // Composes SEVERAL Objects into ONE new Audio Object.
    //
    // NO CONTAINER, and no first among them. This took a container plus
    // children, so the first Object placed in an editing space became the
    // container: it sat at the origin, could not be positioned, and - when such
    // a composition was later nested - occupied exactly the same point as the
    // Object holding it.
    //
    // What the placed Objects are placed IN is the new Object's own spatial
    // attitude: the origin, unrotated. That is the frame, and nothing among them
    // has to serve as it.
    //
    // Composing ONE Object is legitimate and produces a Basic Object holding it:
    // a new identity for a new thing, which is how something is cloned to a
    // different place.
    public RepositoryAsset Compose(
        IReadOnlyList<(string ChildId, SpaceTime? Placement)> placed,
        PointOfView? listenerPointOfView = null)
    {
        if (placed.Count == 0)
            throw new InvalidOperationException("Nothing was placed.");

        var basicEntries = new List<BasicAudioObjectEntry>();
        var subEntries   = new List<SubAudioObjectEntry>();

        var newId = NextId("AUO");

        foreach (var (childId, placement) in placed)
        {
            if (!storage.Exists(childId))
                throw new InvalidOperationException($"{childId} does not exist.");

            var childIsBao = IsType(childId, "BAO");
            var childIsAuo = IsType(childId, "AUO");
            if (!childIsBao && !childIsAuo)
                throw new InvalidOperationException($"{childId} is not a BAO or an AudioObject.");

            PutReference(newId, childId);

            if (childIsBao)
            {
                basicEntries.Add(new BasicAudioObjectEntry
                {
                    BasicAudioObjectSpaceTime = placement,
                    BAObjectIDOrBAObject      = IdStubBao(childId)
                });
            }
            else
            {
                subEntries.Add(new SubAudioObjectEntry
                {
                    SubAudioObjectSpaceTime  = placement,
                    SubAObjectIDOrSubAObject = IdStubAuo(childId)
                });
            }
        }

        storage.Put(newId, Serialize(new AudioObject
        {
            MInstanceID = mInstanceId,
            AudioObjectID = newId,

            // The frame the placed Objects sit in.
            AudioObjectSpaceTime = AtTheOrigin(),

            UserPoV = listenerPointOfView,
            BasicAudioObjectCount = basicEntries.Count,
            BasicAudioObjects = basicEntries.Count > 0 ? basicEntries : null,
            SubAudioObjectCount = subEntries.Count,
            SubAudioObjects = subEntries.Count > 0 ? subEntries : null
        }));

        return new RepositoryAsset { AssetId = newId, AssetType = AssetType.AUO };
    }


    // EVERYTHING Object Edit can change, in ONE new version.
    //
    // The window called EditObjectProperties, then EditObjectDescription, then
    // Rearrange - three keys for one press of OK, which is the very fault
    // Rearrange was added to avoid, committed one level up. An edit is one act
    // and costs one Object.
    //
    // A null argument means "leave it alone", as everywhere else here.
    public RepositoryAsset EditObject(
        string audioObjectAssetId,
        AcousticProfile? acousticProfile = null,
        string? descrMetadata = null,
        IReadOnlyDictionary<string, SpaceTime?>? placements = null,
        PointOfView? listenerPointOfView = null)
    {
        if (!storage.Exists(audioObjectAssetId))
            throw new InvalidOperationException($"{audioObjectAssetId} does not exist.");
        if (!IsType(audioObjectAssetId, "AUO"))
            throw new InvalidOperationException($"{audioObjectAssetId} is not an AudioObject.");

        var existingData = Deserialize<AudioObject>(storage.Get(audioObjectAssetId));

        var basicEntries = (existingData.BasicAudioObjects ?? new List<BasicAudioObjectEntry>())
            .Select(entry =>
            {
                var childId = entry.BAObjectIDOrBAObject?.BasicAudioObjectID ?? "";

                return placements is not null && placements.TryGetValue(childId, out var placement)
                    ? new BasicAudioObjectEntry
                      {
                          BasicAudioObjectSpaceTime = placement,
                          BAObjectIDOrBAObject      = entry.BAObjectIDOrBAObject
                      }
                    : entry;
            })
            .ToList();

        var subEntries = (existingData.SubAudioObjects ?? new List<SubAudioObjectEntry>())
            .Select(entry =>
            {
                var childId = entry.SubAObjectIDOrSubAObject?.AudioObjectID ?? "";

                return placements is not null && placements.TryGetValue(childId, out var placement)
                    ? new SubAudioObjectEntry
                      {
                          SubAudioObjectSpaceTime  = placement,
                          SubAObjectIDOrSubAObject = entry.SubAObjectIDOrSubAObject
                      }
                    : entry;
            })
            .ToList();

        var newId = NextId("AUO");

        storage.Put(newId, Serialize(new AudioObject
        {
            MInstanceID = mInstanceId,
            AudioObjectID = newId,
            AudioObjectTime = existingData.AudioObjectTime,
            AudioObjectSpaceTime = existingData.AudioObjectSpaceTime,
            UserPoV = listenerPointOfView ?? existingData.UserPoV,
            ParentAudioObjectIDs = existingData.ParentAudioObjectIDs,
            DescrMetadata = descrMetadata ?? existingData.DescrMetadata,
            BasicAudioObjectCount = basicEntries.Count,
            BasicAudioObjects = basicEntries.Count > 0 ? basicEntries : null,
            SubAudioObjectCount = subEntries.Count,
            SubAudioObjects = subEntries.Count > 0 ? subEntries : null
        }));

        foreach (var referenced in References(audioObjectAssetId))
            PutReference(newId, referenced);

        return new RepositoryAsset { AssetId = newId, AssetType = AssetType.AUO };
    }

    // The same for a Basic Object, which has no placements to rewrite.
    public RepositoryAsset EditBasicObject(
        string basicAudioObjectAssetId,
        AcousticProfile? acousticProfile = null,
        string? descrMetadata = null,
        PointOfView? listenerPointOfView = null)
    {
        if (!storage.Exists(basicAudioObjectAssetId))
            throw new InvalidOperationException($"{basicAudioObjectAssetId} does not exist.");
        if (!IsType(basicAudioObjectAssetId, "BAO"))
            throw new InvalidOperationException($"{basicAudioObjectAssetId} is not a BasicAudioObject.");

        var existingData = Deserialize<BasicAudioObject>(storage.Get(basicAudioObjectAssetId));
        var existingProps = existingData.BasicAudioObjectProperties ?? new BasicAudioObjectProperties();

        var newId = NextId("BAO");

        storage.Put(newId, Serialize(new BasicAudioObject
        {
            Header = existingData.Header,
            MInstanceID = existingData.MInstanceID,
            UEnvironmentID = existingData.UEnvironmentID,
            BasicAudioObjectID = newId,
            BasicAudioObjectTime = existingData.BasicAudioObjectTime,
            ParentObjects = existingData.ParentObjects,
            ChildObjects = existingData.ChildObjects,
            BasicAudioObjectData = existingData.BasicAudioObjectData,
            UserPoV = listenerPointOfView ?? existingData.UserPoV,
            BasicAudioObjectProperties = new BasicAudioObjectProperties
            {
                BasicAudioObjectSpaceTime = existingProps.BasicAudioObjectSpaceTime,
                Level = existingProps.Level,
                PerceptStatus = existingProps.PerceptStatus,
                AcousticProfile = acousticProfile ?? existingProps.AcousticProfile,
                BasicAudioObjectIdentifier = existingProps.BasicAudioObjectIdentifier
            },
            AudioQualifier = existingData.AudioQualifier,
            DataXMData = existingData.DataXMData,
            DescrMetadata = descrMetadata ?? existingData.DescrMetadata
        }));

        return new RepositoryAsset { AssetId = newId, AssetType = AssetType.BAO };
    }

    // Rewrites EVERY child's placement at once, producing ONE new version.
    //
    // MoveSubObject mints a key per call, so adjusting four components through
    // it would leave three versions nobody asked for. Refining an arrangement is
    // one act and should cost one Object.
    //
    // The children themselves are untouched: what changes is where the container
    // says each of them sits.
    public RepositoryAsset Rearrange(
        string audioObjectAssetId,
        IReadOnlyDictionary<string, SpaceTime?> placements)
    {
        if (!storage.Exists(audioObjectAssetId))
            throw new InvalidOperationException($"{audioObjectAssetId} does not exist.");
        if (!IsType(audioObjectAssetId, "AUO"))
            throw new InvalidOperationException($"{audioObjectAssetId} is not an AudioObject.");

        var existingData = Deserialize<AudioObject>(storage.Get(audioObjectAssetId));

        var basicEntries = (existingData.BasicAudioObjects ?? new List<BasicAudioObjectEntry>())
            .Select(entry =>
            {
                var childId = entry.BAObjectIDOrBAObject?.BasicAudioObjectID ?? "";

                return placements.TryGetValue(childId, out var placement)
                    ? new BasicAudioObjectEntry
                      {
                          BasicAudioObjectSpaceTime = placement,
                          BAObjectIDOrBAObject      = entry.BAObjectIDOrBAObject
                      }
                    : entry;
            })
            .ToList();

        var subEntries = (existingData.SubAudioObjects ?? new List<SubAudioObjectEntry>())
            .Select(entry =>
            {
                var childId = entry.SubAObjectIDOrSubAObject?.AudioObjectID ?? "";

                return placements.TryGetValue(childId, out var placement)
                    ? new SubAudioObjectEntry
                      {
                          SubAudioObjectSpaceTime  = placement,
                          SubAObjectIDOrSubAObject = entry.SubAObjectIDOrSubAObject
                      }
                    : entry;
            })
            .ToList();

        var newId = NextId("AUO");

        storage.Put(newId, Serialize(new AudioObject
        {
            MInstanceID = mInstanceId,
            AudioObjectID = newId,
            AudioObjectTime = existingData.AudioObjectTime,
            AudioObjectSpaceTime = existingData.AudioObjectSpaceTime,
            UserPoV = existingData.UserPoV,
            ParentAudioObjectIDs = existingData.ParentAudioObjectIDs,
            DescrMetadata = existingData.DescrMetadata,
            BasicAudioObjectCount = basicEntries.Count,
            BasicAudioObjects = basicEntries.Count > 0 ? basicEntries : null,
            SubAudioObjectCount = subEntries.Count,
            SubAudioObjects = subEntries.Count > 0 ? subEntries : null
        }));

        foreach (var referenced in References(audioObjectAssetId))
            PutReference(newId, referenced);

        return new RepositoryAsset { AssetId = newId, AssetType = AssetType.AUO };
    }

    // Moves a child WITHIN its container, producing a new version of the
    // container. The child itself is untouched: what changes is where the
    // container says it sits, which is the entry's own SpaceTime.
    //
    // AddSubObject could place a child and nothing could move it afterwards, so
    // a composition was arranged once and then fixed. Every edit mints a new
    // key, as everywhere else here.
    public RepositoryAsset MoveSubObject(
        string audioObjectAssetId,
        string childAssetId,
        SpaceTime? placement)
    {
        if (!storage.Exists(audioObjectAssetId))
            throw new InvalidOperationException($"{audioObjectAssetId} does not exist.");
        if (!IsType(audioObjectAssetId, "AUO"))
            throw new InvalidOperationException($"{audioObjectAssetId} is not an AudioObject.");

        var existingData = Deserialize<AudioObject>(storage.Get(audioObjectAssetId));

        var basicEntries = (existingData.BasicAudioObjects ?? new List<BasicAudioObjectEntry>())
            .Select(entry => entry.BAObjectIDOrBAObject?.BasicAudioObjectID == childAssetId
                ? new BasicAudioObjectEntry
                  {
                      BasicAudioObjectSpaceTime = placement,
                      BAObjectIDOrBAObject      = entry.BAObjectIDOrBAObject
                  }
                : entry)
            .ToList();

        var subEntries = (existingData.SubAudioObjects ?? new List<SubAudioObjectEntry>())
            .Select(entry => entry.SubAObjectIDOrSubAObject?.AudioObjectID == childAssetId
                ? new SubAudioObjectEntry
                  {
                      SubAudioObjectSpaceTime  = placement,
                      SubAObjectIDOrSubAObject = entry.SubAObjectIDOrSubAObject
                  }
                : entry)
            .ToList();

        var newId = NextId("AUO");

        storage.Put(newId, Serialize(new AudioObject
        {
            MInstanceID = mInstanceId,
            AudioObjectID = newId,
            AudioObjectTime = existingData.AudioObjectTime,
            AudioObjectSpaceTime = existingData.AudioObjectSpaceTime,
            UserPoV = existingData.UserPoV,
            ParentAudioObjectIDs = existingData.ParentAudioObjectIDs,
            BasicAudioObjectCount = basicEntries.Count,
            BasicAudioObjects = basicEntries.Count > 0 ? basicEntries : null,
            SubAudioObjectCount = subEntries.Count,
            SubAudioObjects = subEntries.Count > 0 ? subEntries : null
        }));

        foreach (var referenced in References(audioObjectAssetId))
            PutReference(newId, referenced);

        return new RepositoryAsset { AssetId = newId, AssetType = AssetType.AUO };
    }

    // A cycle would form if rootId is reachable by walking down from
    // candidateChildId's own sub-object tree. Only SubAudioObjects matter -
    // BasicAudioObjects/BAOs are always leaves and can never participate in
    // a cycle.
    private bool WouldCreateCycle(string rootId, string candidateChildId)
    {
        if (rootId == candidateChildId) return true;
        if (!IsType(candidateChildId, "AUO") || !storage.Exists(candidateChildId)) return false;

        var data = Deserialize<AudioObject>(storage.Get(candidateChildId));
        foreach (var entry in data.SubAudioObjects ?? new List<SubAudioObjectEntry>())
        {
            var grandchildId = entry.SubAObjectIDOrSubAObject?.AudioObjectID;
            if (grandchildId != null && WouldCreateCycle(rootId, grandchildId))
                return true;
        }
        return false;
    }

    // Produces a new version of a BasicAudioObject with updated properties
    // (Level, PerceptStatus, AcousticProfile, BasicAudioObjectIdentifier).
    // Unspecified (null) parameters keep their existing value - this edits,
    // it does not replace. The audio DATA itself is never touched here,
    // consistent with "BAOs are static" - only the descriptive properties
    // that sit alongside the data change, and that change is itself a new,
    // immutable key, same as everywhere else.
    // listenerPointOfView is where the listener stands relative to this Object.
    //
    // A lone Basic Object is AT THE ORIGIN - it is the thing being auditioned,
    // and there is nothing to move it relative to. What the user may move is the
    // EAR, which is why the schema puts UserPoV on the Basic Object
    // itself. Auditioning one Object is listening to a scene of one; when the
    // Object is later placed in a real Scene, the Scene's listener overrides
    // this one, per the rule that a context overrides what it provides.
    public RepositoryAsset EditBasicObjectProperties(
        string basicAudioObjectAssetId,
        double? level = null,
        bool? perceptStatus = null,
        AcousticProfile? acousticProfile = null,
        InstanceIdentifier? identifier = null,
        PointOfView? listenerPointOfView = null)
    {
        if (!storage.Exists(basicAudioObjectAssetId))
            throw new InvalidOperationException($"{basicAudioObjectAssetId} does not exist.");
        if (!IsType(basicAudioObjectAssetId, "BAO"))
            throw new InvalidOperationException($"{basicAudioObjectAssetId} is not a BasicAudioObject.");

        var existingData = Deserialize<BasicAudioObject>(storage.Get(basicAudioObjectAssetId));
        var existingProps = existingData.BasicAudioObjectProperties ?? new BasicAudioObjectProperties();

        var updatedProps = new BasicAudioObjectProperties
        {
            BasicAudioObjectSpaceTime = existingProps.BasicAudioObjectSpaceTime,
            Level = level ?? existingProps.Level,
            PerceptStatus = perceptStatus ?? existingProps.PerceptStatus,
            AcousticProfile = acousticProfile ?? existingProps.AcousticProfile,
            BasicAudioObjectIdentifier = identifier ?? existingProps.BasicAudioObjectIdentifier
        };

        var newId = NextId("BAO");
        storage.Put(newId, Serialize(new BasicAudioObject
        {
            Header = existingData.Header,
            MInstanceID = existingData.MInstanceID,
            UEnvironmentID = existingData.UEnvironmentID,
            BasicAudioObjectID = newId,
            BasicAudioObjectTime = existingData.BasicAudioObjectTime,
            ParentObjects = existingData.ParentObjects,
            ChildObjects = existingData.ChildObjects,
            BasicAudioObjectData = existingData.BasicAudioObjectData,
            UserPoV = listenerPointOfView ?? existingData.UserPoV,
            BasicAudioObjectProperties = updatedProps,
            AudioQualifier = existingData.AudioQualifier,
            DataXMData = existingData.DataXMData,
            DescrMetadata = existingData.DescrMetadata
        }));

        return new RepositoryAsset { AssetId = newId, AssetType = AssetType.BAO };
    }

    // Produces a new version of an AudioObject with an updated
    // AudioObjectProperties (AcousticProfile) and/or ParentAudioObjectIDs.
    // placement edits the object's own intrinsic AudioObjectSpaceTime -
    // Mode 1 (AUO editing): a user places THIS object at a position
    // independent of any scene, distinct from ASE's per-placement position
    // when the same object is later put into a scene.
    // listenerPointOfView is where the Object is being listened FROM, which the
    // schema calls UserPoV. A composed Object needs it for the same reason a
    // Basic one does: auditioning is listening to a scene of one, and what moves
    // is the ear.
    public RepositoryAsset EditObjectProperties(
        string audioObjectAssetId,
        AcousticProfile? acousticProfile = null,
        List<string>? parentAudioObjectIDs = null,
        SpaceTime? placement = null,
        PointOfView? listenerPointOfView = null)
    {
        if (!storage.Exists(audioObjectAssetId))
            throw new InvalidOperationException($"{audioObjectAssetId} does not exist.");
        if (!IsType(audioObjectAssetId, "AUO"))
            throw new InvalidOperationException($"{audioObjectAssetId} is not an AudioObject.");

        // AN AUDIO OBJECT HAS NO ACOUSTIC PROFILE (the author, 2026/10/02): as the
        // Qualifier, it belongs to the Basic Objects, each with its own.
        if (acousticProfile is not null)
            throw new InvalidOperationException("An Audio Object has no Acoustic Profile: its Basic Audio Objects have one each.");

        var existingData = Deserialize<AudioObject>(storage.Get(audioObjectAssetId));

        var newId = NextId("AUO");
        storage.Put(newId, Serialize(new AudioObject
        {
            MInstanceID = mInstanceId,
            AudioObjectID = newId,
            AudioObjectTime = existingData.AudioObjectTime,
            AudioObjectSpaceTime = placement ?? existingData.AudioObjectSpaceTime,
            UserPoV = listenerPointOfView ?? existingData.UserPoV,
            ParentAudioObjectIDs = parentAudioObjectIDs ?? existingData.ParentAudioObjectIDs,
            BasicAudioObjectCount = existingData.BasicAudioObjectCount,
            BasicAudioObjects = existingData.BasicAudioObjects,
            SubAudioObjectCount = existingData.SubAudioObjectCount,
            SubAudioObjects = existingData.SubAudioObjects
        }));

        return new RepositoryAsset { AssetId = newId, AssetType = AssetType.AUO };
    }

    // The name and description a person wrote, both carried in DescrMetadata
    // with the first line serving as the name.
    //
    // This is an INTERNAL characteristic - what the Object is, not where it
    // stands - so it mints a new version, as every edit to what a thing is does.
    // The schemas have no Name field; adding one to every Data Type a person
    // handles is a question for MPAI rather than a change to make here, and
    // storing a name in the identifier would be worse: an identifier is
    // machine-assigned and stable, a name is human and changeable, and
    // conflating them means renaming breaks every reference.
    public RepositoryAsset EditBasicObjectDescription(
        string basicAudioObjectAssetId,
        string? descrMetadata)
    {
        if (!storage.Exists(basicAudioObjectAssetId))
            throw new InvalidOperationException($"{basicAudioObjectAssetId} does not exist.");
        if (!IsType(basicAudioObjectAssetId, "BAO"))
            throw new InvalidOperationException($"{basicAudioObjectAssetId} is not a BasicAudioObject.");

        var existingData = Deserialize<BasicAudioObject>(storage.Get(basicAudioObjectAssetId));

        var newId = NextId("BAO");

        storage.Put(newId, Serialize(new BasicAudioObject
        {
            Header = existingData.Header,
            MInstanceID = existingData.MInstanceID,
            UEnvironmentID = existingData.UEnvironmentID,
            BasicAudioObjectID = newId,
            BasicAudioObjectTime = existingData.BasicAudioObjectTime,
            ParentObjects = existingData.ParentObjects,
            ChildObjects = existingData.ChildObjects,
            BasicAudioObjectData = existingData.BasicAudioObjectData,
            UserPoV = existingData.UserPoV,
            BasicAudioObjectProperties = existingData.BasicAudioObjectProperties,
            AudioQualifier = existingData.AudioQualifier,
            DataXMData = existingData.DataXMData,
            DescrMetadata = descrMetadata
        }));

        return new RepositoryAsset { AssetId = newId, AssetType = AssetType.BAO };
    }

    public RepositoryAsset EditObjectDescription(
        string audioObjectAssetId,
        string? descrMetadata)
    {
        if (!storage.Exists(audioObjectAssetId))
            throw new InvalidOperationException($"{audioObjectAssetId} does not exist.");
        if (!IsType(audioObjectAssetId, "AUO"))
            throw new InvalidOperationException($"{audioObjectAssetId} is not an AudioObject.");

        var existingData = Deserialize<AudioObject>(storage.Get(audioObjectAssetId));

        var newId = NextId("AUO");

        storage.Put(newId, Serialize(new AudioObject
        {
            MInstanceID = mInstanceId,
            AudioObjectID = newId,
            AudioObjectTime = existingData.AudioObjectTime,
            AudioObjectSpaceTime = existingData.AudioObjectSpaceTime,
            UserPoV = existingData.UserPoV,
            ParentAudioObjectIDs = existingData.ParentAudioObjectIDs,
            BasicAudioObjectCount = existingData.BasicAudioObjectCount,
            BasicAudioObjects = existingData.BasicAudioObjects,
            SubAudioObjectCount = existingData.SubAudioObjectCount,
            SubAudioObjects = existingData.SubAudioObjects,
            DescrMetadata = descrMetadata
        }));

        foreach (var referenced in References(audioObjectAssetId))
            PutReference(newId, referenced);

        return new RepositoryAsset { AssetId = newId, AssetType = AssetType.AUO };
    }

    // Resolves a stored AudioObject into a fully self-contained structure -
    // its own fields plus every BAO/sub-AudioObject it (transitively)
    // references, with each ID-stub entry replaced by its full, live
    // content. The stored value itself is already schema-valid before this
    // resolution happens; this is what an external consumer (ASD delivery,
    // export) needs.
    // Does this asset exist? Needed by the AIF-facing half to tell an OPEN from a
    // CREATE: a Basic Audio Object arriving with an identifier already in the
    // repository is being opened for editing; one without is new. Without this,
    // an existing Basic Audio Object could never be edited, only replaced.
    public bool Has(string assetId) =>
        !string.IsNullOrWhiteSpace(assetId) && storage.Exists(assetId);

    // A stored Basic Audio Object, as it is.
    public BasicAudioObject Get(string basicAudioObjectAssetId) =>
        IsType(basicAudioObjectAssetId, "BAO") && storage.Exists(basicAudioObjectAssetId)
            ? Deserialize<BasicAudioObject>(storage.Get(basicAudioObjectAssetId))
            : throw new InvalidOperationException($"{basicAudioObjectAssetId} is not a stored Basic Audio Object.");

    public AudioObject Materialize(string audioObjectAssetId)
    {
        if (!storage.Exists(audioObjectAssetId))
            throw new InvalidOperationException($"{audioObjectAssetId} does not exist.");

        // A BASIC Object materialises as itself: an Object of one, resolved so
        // that a consumer needs no separate case for it. Callers ask for the
        // content of what is open without first asking which kind it is.
        if (IsType(audioObjectAssetId, "BAO"))
        {
            var basic = Deserialize<BasicAudioObject>(storage.Get(audioObjectAssetId));

            return new AudioObject
            {
                MInstanceID = mInstanceId,
                AudioObjectID = audioObjectAssetId,
                BasicAudioObjectCount = 1,
                BasicAudioObjects = new List<BasicAudioObjectEntry>
                {
                    new BasicAudioObjectEntry { BAObjectIDOrBAObject = basic }
                }
            };
        }

        if (!IsType(audioObjectAssetId, "AUO"))
            throw new InvalidOperationException($"{audioObjectAssetId} is not a BAO or an AudioObject.");

        var audioObject = Deserialize<AudioObject>(storage.Get(audioObjectAssetId));

        var basicChildren = new List<BasicAudioObjectEntry>();
        var subChildren = new List<SubAudioObjectEntry>();

        foreach (var entry in audioObject.BasicAudioObjects ?? new List<BasicAudioObjectEntry>())
        {
            var childId = entry.BAObjectIDOrBAObject?.BasicAudioObjectID;
            if (childId is null || !storage.Exists(childId)) continue;

            var bao = Deserialize<BasicAudioObject>(storage.Get(childId));
            basicChildren.Add(new BasicAudioObjectEntry { BasicAudioObjectSpaceTime = entry.BasicAudioObjectSpaceTime, BAObjectIDOrBAObject = bao });
        }

        foreach (var entry in audioObject.SubAudioObjects ?? new List<SubAudioObjectEntry>())
        {
            var childId = entry.SubAObjectIDOrSubAObject?.AudioObjectID;
            if (string.IsNullOrEmpty(childId) || !storage.Exists(childId) || !IsType(childId, "AUO")) continue;

            subChildren.Add(new SubAudioObjectEntry { SubAudioObjectSpaceTime = entry.SubAudioObjectSpaceTime, SubAObjectIDOrSubAObject = Materialize(childId) });
        }

        return new AudioObject
        {
            MInstanceID = mInstanceId,
            // AssetId is always authoritative for identity - the domain
            // payload's own AudioObjectID has no independent way of being
            // kept in sync, so it is not trusted here.
            AudioObjectID = audioObjectAssetId,
            AudioObjectTime = audioObject.AudioObjectTime,
            AudioObjectSpaceTime = audioObject.AudioObjectSpaceTime,
            ParentAudioObjectIDs = audioObject.ParentAudioObjectIDs,
            BasicAudioObjectCount = basicChildren.Count,
            BasicAudioObjects = basicChildren.Count > 0 ? basicChildren : null,
            SubAudioObjectCount = subChildren.Count,
            SubAudioObjects = subChildren.Count > 0 ? subChildren : null
        };
    }

    // --- Deletion (surfaces the sixth Shared Storage primitive, Delete) ---
    // One-level reference set for an asset: every id it references
    // (ref:{id}:*) and every id that references it (refby:{id}:*). Used by
    // the UI to decide whether a Delete is safe and, if not, to show and
    // optionally cascade over exactly the assets involved - one level only.
    public IReadOnlyList<string> ReferencedBy(string assetId) =>
        storage.List($"refby:{assetId}:").Select(k => k[$"refby:{assetId}:".Length..]).ToList();

    public IReadOnlyList<string> References(string assetId) =>
        storage.List($"ref:{assetId}:").Select(k => k[$"ref:{assetId}:".Length..]).ToList();

    // Deletes a single asset key together with its own ref:/refby: bookkeeping
    // (both directions), per Section 4.3 of the Shared Storage proposal.
    // Deleting a non-existent key is not an error (Section 4.10.3). Does NOT
    // itself cascade - the caller decides, one level, with user consent.
    public void Delete(string assetId)
    {
        foreach (var toId in References(assetId))
        {
            storage.Delete($"ref:{assetId}:{toId}");
            storage.Delete($"refby:{toId}:{assetId}");
        }
        foreach (var fromId in ReferencedBy(assetId))
        {
            storage.Delete($"ref:{fromId}:{assetId}");
            storage.Delete($"refby:{assetId}:{fromId}");
        }
        storage.Delete(assetId);
    }
}