using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using AIF.SharedStorage;
using Mpai.Cae.Aoe;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Repository;

namespace Mpai.Cae.Ase;

// ---------------------------------------------------------------------------
//  CAE-ASE-V1.0 - Audio Scene Editing.
//
//  Ported directly onto the proposed MPAI-AIF Shared Storage API, same
//  approach as AoeAim: no intermediate Repository class or method
//  vocabulary, AssetId doubles as its own Shared Storage key.
//
//  Two real, separate paths, not one path pretending to cover both:
//
//   - CreateScene/AddObjectToScene/Materialize build AudioSceneDescriptors
//     (ASD - the full/hierarchical scene type), composing AudioObjects.
//   - CreateBasicScene/AddObjectToBasicScene/MaterializeBasicScene build
//     BasicAudioSceneDescriptors (BAS), composing BasicAudioObjects
//     directly - the genuinely simple case, not an artificially-wrapped
//     AudioObject standing in for one.
// ---------------------------------------------------------------------------
public sealed class AseAim
{
    private readonly ISharedStorage storage;

    // NO AoeAim HERE ANY MORE.
    //
    // This class used to hold one and call aoe.Materialize() to expand each
    // child object of a scene - one AIM invoking another directly, with no
    // Controller between them and nothing in any AMD saying it happened. The
    // Topology has always said CAE-AOE feeds CAE-ASE; the code went around it.
    //
    // Materialize now takes a resolver, so whoever runs this AIM decides where
    // an expanded object comes from. AseAimProcessor supplies one backed by what
    // has arrived on its AudioObject Port - which is the Topology, honoured.
    // The M-Instance or U-Environment the Scenes are made in: OSD requires the first
    // on a Basic Audio Scene. An application gives its own; ASM's own is "ASM".
    private readonly string mInstanceId;

    public AseAim(ISharedStorage storage, string mInstanceId = "ASM")
    {
        this.storage = storage;
        this.mInstanceId = mInstanceId;
    }

    // A Scene not yet placed is at the origin of its space.
    private static SpaceTime Origin() => new()
    {
        SpaceTimeID = Guid.NewGuid().ToString(),
        SpatialAttitude1 = new SpatialAttitude
        {
            ObjectSpatialAttitudeID = Guid.NewGuid().ToString(),
            Position = new Position { PositionID = Guid.NewGuid().ToString(), CartPosition = [0, 0, 0] },
            Orientation = new Orientation { OrientationID = Guid.NewGuid().ToString(), EulerAngles = [0, 0, 0] }
        }
    };

    private static readonly JsonSerializerOptions DataOptions = new() { Converters = { new JsonStringEnumConverter() } };
    private static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, DataOptions);
    private static T Deserialize<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, DataOptions)!;

    private string NextId(string typePrefix)
    {
        var counterKey = $"_counter:{typePrefix}";
        long next = storage.Exists(counterKey)
            ? long.Parse(Encoding.UTF8.GetString(storage.Get(counterKey))) + 1
            : 1;
        storage.Put(counterKey, Encoding.UTF8.GetBytes(next.ToString()));
        return $"{typePrefix}{next:D6}";
    }

    private void PutReference(string fromId, string toId)
    {
        storage.Put($"ref:{fromId}:{toId}", Array.Empty<byte>());
        storage.Put($"refby:{toId}:{fromId}", Array.Empty<byte>());
    }

    private static bool IsType(string assetId, string prefix) => assetId.StartsWith(prefix, StringComparison.Ordinal);

    // Wraps a single AudioObject into a new, minimal scene (the degenerate
    // case: one placement, no sub-scenes). placement is optional - null
    // means "no position/time recorded for this placement." listenerPointOfView
    // is optional too - the scene can be created before the listener is placed.
    public RepositoryAsset CreateScene(string audioObjectAssetId, SpaceTime? placement = null, PointOfView? listenerPointOfView = null)
    {
        if (!storage.Exists(audioObjectAssetId))
            throw new InvalidOperationException($"{audioObjectAssetId} does not exist.");
        if (!IsType(audioObjectAssetId, "AUO"))
            throw new InvalidOperationException($"{audioObjectAssetId} is not an AudioObject.");

        var sceneId = NextId("ASD");
        storage.Put(sceneId, Serialize(new AudioSceneDescriptors
        {
            MInstanceID = mInstanceId,
            AudioSceneDescriptorsID = sceneId,
            AudioSceneDescriptorsSpaceTime = Origin(),
            UserPoV = listenerPointOfView,
            AudioObjectCount = 1,
            AudioObjects = new() { MakeEntry(audioObjectAssetId, placement) }
        }));

        PutReference(sceneId, audioObjectAssetId);

        return new RepositoryAsset { AssetId = sceneId, AssetType = AssetType.ASD };
    }

    // Adds an existing AudioObject as a further placement in an existing
    // scene. Produces a NEW key for the scene (no-cascade Save rule: the
    // object being added is untouched). listenerPointOfView, if given,
    // updates the scene's listener at the same time; if null, the existing
    // listener (if any) carries forward unchanged.
    public RepositoryAsset AddObjectToScene(string sceneAssetId, string audioObjectAssetId, SpaceTime? placement = null, PointOfView? listenerPointOfView = null)
    {
        if (!storage.Exists(sceneAssetId))
            throw new InvalidOperationException($"{sceneAssetId} does not exist.");
        if (!IsType(sceneAssetId, "ASD"))
            throw new InvalidOperationException($"{sceneAssetId} is not a Scene.");

        if (!storage.Exists(audioObjectAssetId))
            throw new InvalidOperationException($"{audioObjectAssetId} does not exist.");
        if (!IsType(audioObjectAssetId, "AUO"))
            throw new InvalidOperationException($"{audioObjectAssetId} is not an AudioObject.");

        var existingData = Deserialize<AudioSceneDescriptors>(storage.Get(sceneAssetId));

        var newId = NextId("ASD");
        PutReference(newId, audioObjectAssetId);

        var entries = (existingData.AudioObjects ?? new List<AudioSceneObjectEntry>()).ToList();
        entries.Add(MakeEntry(audioObjectAssetId, placement));

        storage.Put(newId, Serialize(new AudioSceneDescriptors
        {
            MInstanceID = mInstanceId,
            AudioSceneDescriptorsID = newId,
            AudioSceneDescriptorsTime = existingData.AudioSceneDescriptorsTime,
            AudioSceneDescriptorsSpaceTime = existingData.AudioSceneDescriptorsSpaceTime ?? Origin(),
            UserPoV = listenerPointOfView ?? existingData.UserPoV,
            AudioObjectCount = entries.Count,
            AudioObjects = entries,
            SubAudioSceneCount = existingData.SubAudioSceneCount,
            SubAudioScenes = existingData.SubAudioScenes
        }));

        return new RepositoryAsset { AssetId = newId, AssetType = AssetType.ASD };
    }

    // Updates just the scene's listener, without adding or moving any
    // object placement - what "drag the listener marker on the canvas"
    // ultimately calls. Produces a new key, same no-cascade rule as
    // everything else.
    public RepositoryAsset SetSceneListener(string sceneAssetId, PointOfView listenerPointOfView)
    {
        if (!storage.Exists(sceneAssetId))
            throw new InvalidOperationException($"{sceneAssetId} does not exist.");
        if (!IsType(sceneAssetId, "ASD"))
            throw new InvalidOperationException($"{sceneAssetId} is not a Scene.");

        var existingData = Deserialize<AudioSceneDescriptors>(storage.Get(sceneAssetId));

        var newId = NextId("ASD");
        storage.Put(newId, Serialize(new AudioSceneDescriptors
        {
            MInstanceID = mInstanceId,
            AudioSceneDescriptorsID = newId,
            AudioSceneDescriptorsTime = existingData.AudioSceneDescriptorsTime,
            AudioSceneDescriptorsSpaceTime = existingData.AudioSceneDescriptorsSpaceTime ?? Origin(),
            UserPoV = listenerPointOfView,
            AudioObjectCount = existingData.AudioObjectCount,
            AudioObjects = existingData.AudioObjects,
            SubAudioSceneCount = existingData.SubAudioSceneCount,
            SubAudioScenes = existingData.SubAudioScenes
        }));

        return new RepositoryAsset { AssetId = newId, AssetType = AssetType.ASD };
    }

    private static AudioSceneObjectEntry MakeEntry(string audioObjectAssetId, SpaceTime? placement) => new()
    {
        ObjectIDOrObject = new AudioObject { AudioObjectID = audioObjectAssetId },
        AudioObjectSpaceTime = placement
    };

    // Resolves a stored scene into a fully self-contained structure: its
    // own fields, with every ID-stub AudioObject entry replaced by its
    // full, live content (via AoeAim.Materialize), and each entry's real
    // AudioObjectSpaceTime carried across unchanged.
    // resolveObject supplies the expanded form of a child AudioObject. Passing
    // null leaves each child as the identifier it already is, which
    // AudioSceneObjectEntry permits - it carries an ObjectOrID.
    public AudioSceneDescriptors Materialize(
        string sceneAssetId,
        Func<string, AudioObject?>? resolveObject = null)
    {
        if (!storage.Exists(sceneAssetId))
            throw new InvalidOperationException($"{sceneAssetId} does not exist.");
        if (!IsType(sceneAssetId, "ASD"))
            throw new InvalidOperationException($"{sceneAssetId} is not a Scene.");

        var scene = Deserialize<AudioSceneDescriptors>(storage.Get(sceneAssetId));

        var objectEntries = new List<AudioSceneObjectEntry>();

        foreach (var entry in scene.AudioObjects ?? new List<AudioSceneObjectEntry>())
        {
            var childId = entry.ObjectIDOrObject?.AudioObjectID;
            if (string.IsNullOrEmpty(childId) || !storage.Exists(childId) || !IsType(childId, "AUO")) continue;

            objectEntries.Add(new AudioSceneObjectEntry
            {
                ObjectIDOrObject = resolveObject?.Invoke(childId)
                                   ?? new AudioObject { AudioObjectID = childId },
                AudioObjectSpaceTime = entry.AudioObjectSpaceTime
            });
        }

        return new AudioSceneDescriptors
        {
            MInstanceID = mInstanceId,
            AudioSceneDescriptorsID = sceneAssetId,
            AudioSceneDescriptorsTime = scene.AudioSceneDescriptorsTime,
            AudioSceneDescriptorsSpaceTime = scene.AudioSceneDescriptorsSpaceTime ?? Origin(),
            UserPoV = scene.UserPoV,
            AudioObjectCount = objectEntries.Count,
            AudioObjects = objectEntries.Count > 0 ? objectEntries : null
        };
    }

    // -----------------------------------------------------------------
    //  BasicAudioSceneDescriptors (BAS) path - a genuinely Basic scene,
    //  composing BasicAudioObjects directly, not AudioObjects.
    // -----------------------------------------------------------------

    // Wraps a single BasicAudioObject into a new, minimal Basic scene.
    // listenerPointOfView is required - BasicAudioSceneDescriptors.json
    // lists it as a required field, unlike ASD which has no such field at
    // all (there, the listener is only ever a delivery-time parameter).
    public RepositoryAsset CreateBasicScene(string basicAudioObjectAssetId, PointOfView listenerPointOfView, SpaceTime? placement = null)
    {
        if (!storage.Exists(basicAudioObjectAssetId))
            throw new InvalidOperationException($"{basicAudioObjectAssetId} does not exist.");
        if (!IsType(basicAudioObjectAssetId, "BAO"))
            throw new InvalidOperationException($"{basicAudioObjectAssetId} is not a BasicAudioObject.");

        var sceneId = NextId("BAS");
        storage.Put(sceneId, Serialize(new BasicAudioSceneDescriptors
        {
            MInstanceID = mInstanceId,
            BasicAudioSceneDescriptorsID = sceneId,
            BASSpaceTime = Origin(),
            UserPoV = listenerPointOfView,
            AudioObjectCount = 1,
            BasicAudioSceneDescriptorsEntries = new() { MakeBasicEntry(basicAudioObjectAssetId, placement) }
        }));

        PutReference(sceneId, basicAudioObjectAssetId);

        return new RepositoryAsset { AssetId = sceneId, AssetType = AssetType.BAS };
    }

    // Adds an existing BasicAudioObject as a further placement in an
    // existing Basic scene. Produces a NEW key (no-cascade Save rule).
    public RepositoryAsset AddObjectToBasicScene(string sceneAssetId, string basicAudioObjectAssetId, SpaceTime? placement = null)
    {
        if (!storage.Exists(sceneAssetId))
            throw new InvalidOperationException($"{sceneAssetId} does not exist.");
        if (!IsType(sceneAssetId, "BAS"))
            throw new InvalidOperationException($"{sceneAssetId} is not a Basic Scene.");

        if (!storage.Exists(basicAudioObjectAssetId))
            throw new InvalidOperationException($"{basicAudioObjectAssetId} does not exist.");
        if (!IsType(basicAudioObjectAssetId, "BAO"))
            throw new InvalidOperationException($"{basicAudioObjectAssetId} is not a BasicAudioObject.");

        var existingData = Deserialize<BasicAudioSceneDescriptors>(storage.Get(sceneAssetId));

        var newId = NextId("BAS");
        PutReference(newId, basicAudioObjectAssetId);

        var entries = existingData.BasicAudioSceneDescriptorsEntries.ToList();
        entries.Add(MakeBasicEntry(basicAudioObjectAssetId, placement));

        storage.Put(newId, Serialize(new BasicAudioSceneDescriptors
        {
            MInstanceID = mInstanceId,
            BasicAudioSceneDescriptorsID = newId,
            BasicAudioSceneDescriptorsTime = existingData.BasicAudioSceneDescriptorsTime,
            BASSpaceTime = existingData.BASSpaceTime ?? Origin(),
            UserPoV = existingData.UserPoV,
            ClosedSpace = existingData.ClosedSpace,
            AcousticProfile = existingData.AcousticProfile,
            AudioObjectCount = entries.Count,
            BasicAudioSceneDescriptorsEntries = entries
        }));

        return new RepositoryAsset { AssetId = newId, AssetType = AssetType.BAS };
    }

    // A member is placed - position and orientation - by its Space-Time alone.
    // The per-member Point of View this once also wrote, a copy of the placement,
    // is now the member's UserPoV (the author, 2026/10/02): where the member is
    // heard from, absent unless set.
    // A placed member moved, or taken out: a NEW version, as every edit. The member is
    // found by its Basic Audio Object's identifier; false when the Scene has none.
    public RepositoryAsset? ReplaceInBasicScene(string sceneAssetId, string basicAudioObjectAssetId, SpaceTime? placement) =>
        RebuildBasicScene(sceneAssetId, basicAudioObjectAssetId, e => new BasicAudioSceneEntry
        {
            AudioObjectIDOrAudioObject = e.AudioObjectIDOrAudioObject, UserPoV = e.UserPoV, AudioObjectSpaceTime = placement ?? e.AudioObjectSpaceTime
        });

    public RepositoryAsset? RemoveFromBasicScene(string sceneAssetId, string basicAudioObjectAssetId) =>
        RebuildBasicScene(sceneAssetId, basicAudioObjectAssetId, _ => null);

    private RepositoryAsset? RebuildBasicScene(string sceneAssetId, string memberId, Func<BasicAudioSceneEntry, BasicAudioSceneEntry?> change)
    {
        if (!storage.Exists(sceneAssetId) || !IsType(sceneAssetId, "BAS"))
            throw new InvalidOperationException($"{sceneAssetId} is not a stored Basic Scene.");
        var existing = Deserialize<BasicAudioSceneDescriptors>(storage.Get(sceneAssetId));
        var found = false;
        var entries = new List<BasicAudioSceneEntry>();
        foreach (var e in existing.BasicAudioSceneDescriptorsEntries)
        {
            if (!found && e.AudioObjectIDOrAudioObject?.BasicAudioObjectID == memberId)
            {
                found = true;
                if (change(e) is { } kept) entries.Add(kept);
            }
            else entries.Add(e);
        }
        if (!found) return null;

        var newId = NextId("BAS");
        foreach (var e in entries)
            if (e.AudioObjectIDOrAudioObject?.BasicAudioObjectID is { } member) PutReference(newId, member);
        storage.Put(newId, Serialize(new BasicAudioSceneDescriptors
        {
            MInstanceID = mInstanceId,
            BasicAudioSceneDescriptorsID = newId,
            BasicAudioSceneDescriptorsTime = existing.BasicAudioSceneDescriptorsTime,
            BASSpaceTime = existing.BASSpaceTime ?? Origin(),
            UserPoV = existing.UserPoV,
            ClosedSpace = existing.ClosedSpace,
            AcousticProfile = existing.AcousticProfile,
            AudioObjectCount = entries.Count,
            BasicAudioSceneDescriptorsEntries = entries
        }));
        return new RepositoryAsset { AssetId = newId, AssetType = AssetType.BAS };
    }

    private static BasicAudioSceneEntry MakeBasicEntry(string basicAudioObjectAssetId, SpaceTime? placement) => new()
    {
        AudioObjectIDOrAudioObject = new BasicAudioObject { BasicAudioObjectID = basicAudioObjectAssetId },
        AudioObjectSpaceTime = placement
    };

    // Sets where a Basic scene is heard from: a NEW version, as every edit.
    public RepositoryAsset SetBasicSceneListener(string sceneAssetId, PointOfView userPoV)
    {
        if (!storage.Exists(sceneAssetId))
            throw new InvalidOperationException($"{sceneAssetId} does not exist.");
        if (!IsType(sceneAssetId, "BAS"))
            throw new InvalidOperationException($"{sceneAssetId} is not a Basic Scene.");

        var existing = Deserialize<BasicAudioSceneDescriptors>(storage.Get(sceneAssetId));
        var newId = NextId("BAS");
        foreach (var entry in existing.BasicAudioSceneDescriptorsEntries)
            if (entry.AudioObjectIDOrAudioObject?.BasicAudioObjectID is { Length: > 0 } member)
                PutReference(newId, member);

        storage.Put(newId, Serialize(new BasicAudioSceneDescriptors
        {
            MInstanceID = mInstanceId,
            BasicAudioSceneDescriptorsID = newId,
            BasicAudioSceneDescriptorsTime = existing.BasicAudioSceneDescriptorsTime,
            BASSpaceTime = existing.BASSpaceTime ?? Origin(),
            UserPoV = userPoV,
            ClosedSpace = existing.ClosedSpace,
            AcousticProfile = existing.AcousticProfile,
            AudioObjectCount = existing.AudioObjectCount,
            BasicAudioSceneDescriptorsEntries = existing.BasicAudioSceneDescriptorsEntries
        }));
        return new RepositoryAsset { AssetId = newId, AssetType = AssetType.BAS };
    }

    // Resolves a stored Basic scene into a fully self-contained structure:
    // its own fields, with every ID-stub BasicAudioObject entry replaced by
    // its full, live content.
    public BasicAudioSceneDescriptors MaterializeBasicScene(string sceneAssetId)
    {
        if (!storage.Exists(sceneAssetId))
            throw new InvalidOperationException($"{sceneAssetId} does not exist.");
        if (!IsType(sceneAssetId, "BAS"))
            throw new InvalidOperationException($"{sceneAssetId} is not a Basic Scene.");

        var scene = Deserialize<BasicAudioSceneDescriptors>(storage.Get(sceneAssetId));

        var resolvedEntries = new List<BasicAudioSceneEntry>();

        foreach (var entry in scene.BasicAudioSceneDescriptorsEntries)
        {
            var childId = entry.AudioObjectIDOrAudioObject?.BasicAudioObjectID;
            if (string.IsNullOrEmpty(childId) || !storage.Exists(childId)) continue;

            var bao = Deserialize<BasicAudioObject>(storage.Get(childId));

            resolvedEntries.Add(new BasicAudioSceneEntry
            {
                AudioObjectIDOrAudioObject = bao,
                AudioObjectSpaceTime = entry.AudioObjectSpaceTime,
                AudioSceneEnrichment = entry.AudioSceneEnrichment,
                UserPoV = entry.UserPoV
            });
        }

        return new BasicAudioSceneDescriptors
        {
            MInstanceID = mInstanceId,
            BasicAudioSceneDescriptorsID = sceneAssetId,
            BasicAudioSceneDescriptorsTime = scene.BasicAudioSceneDescriptorsTime,
            BASSpaceTime = scene.BASSpaceTime ?? Origin(),
            UserPoV = scene.UserPoV,
            ClosedSpace = scene.ClosedSpace,
            AcousticProfile = scene.AcousticProfile,
            AudioObjectCount = resolvedEntries.Count,
            BasicAudioSceneDescriptorsEntries = resolvedEntries
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