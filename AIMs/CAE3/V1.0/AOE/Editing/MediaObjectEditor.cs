using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

using AIF.SharedStorage;

using Mpai.Core;

namespace Mpai.Cae.Editing;

// COMPOSITION, written once for every medium.
//
// Create, add, compose, move, materialise, delete - and the Repository
// bookkeeping underneath them. None of it knows what a sound is: it puts things
// inside things at positions, and keeps references in both directions so that
// nothing is deleted out from under something that holds it.
//
// A medium supplies the rest through IMediaObjectFamily. Audio and Speech do
// today; Visual will cost an adapter rather than a copy of this file, which is
// the whole reason it exists.
//
// EVERY EDIT MINTS A NEW KEY. Nothing here changes an Asset in place: composing,
// moving and editing all produce a new version, and the previous one remains.
// That is what makes "undo" for a saved Object mean "select the earlier one".
public sealed class MediaObjectEditor<TBasic, TFull>
    where TBasic : class
    where TFull : class
{
    private readonly ISharedStorage storage;
    private readonly IMediaObjectFamily<TBasic, TFull> family;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public MediaObjectEditor(ISharedStorage storage, IMediaObjectFamily<TBasic, TFull> family)
    {
        this.storage = storage;
        this.family  = family;
    }

    // ---- what the Repository holds -------------------------------------

    public bool Has(string assetId) =>
        !string.IsNullOrWhiteSpace(assetId) && storage.Exists(assetId);

    public bool IsBasic(string assetId) => IsType(assetId, family.BasicPrefix);
    public bool IsFull(string assetId)  => IsType(assetId, family.FullPrefix);

    public IReadOnlyList<string> References(string assetId) =>
        storage.List($"ref:{assetId}:").Select(k => k[$"ref:{assetId}:".Length..]).ToList();

    public IReadOnlyList<string> ReferencedBy(string assetId) =>
        storage.List($"refby:{assetId}:").Select(k => k[$"refby:{assetId}:".Length..]).ToList();

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

    // ---- creating ------------------------------------------------------

    // Stores a Basic Object, and stops there.
    //
    // An Object holding one Object IS Basic; one holding more than one is full.
    // The kind is a fact about the CONTENT, not a decision taken at creation, so
    // acquiring yields a Basic Object and nothing else. It becomes a full Object
    // when a second is composed into it.
    public string CreateBasic(TBasic basic)
    {
        var id = NextId(family.BasicPrefix);
        storage.Put(id, Serialize(family.WithId(basic, id)));
        return id;
    }

    public TBasic ReadBasic(string assetId) =>
        Deserialize<TBasic>(storage.Get(assetId));

    public TFull ReadFull(string assetId) =>
        Deserialize<TFull>(storage.Get(assetId));

    // ---- composing -----------------------------------------------------

    // Adds several children at once, producing ONE new Object.
    //
    // Adding them one at a time would mint a key per call and leave
    // intermediates nobody asked for - an editing space is a draft, and saving
    // it should produce one Object rather than a trail of them.
    //
    // The container may be BASIC: that is the case which mints the first full
    // Object, and the Basic container joins its own composition as the first
    // entry.
    public string Compose(
        string containerAssetId,
        IReadOnlyList<(string ChildId, SpaceTime? Placement)> children,
        PointOfView? listener = null)
    {
        if (!storage.Exists(containerAssetId))
            throw new InvalidOperationException($"{containerAssetId} does not exist.");

        var containerIsBasic = IsBasic(containerAssetId);
        var containerIsFull  = IsFull(containerAssetId);

        if (!containerIsBasic && !containerIsFull)
            throw new InvalidOperationException($"{containerAssetId} is not a {family.BasicPrefix} or a {family.FullPrefix}.");

        var previous = containerIsFull ? ReadFull(containerAssetId) : null;

        var basicEntries = previous is null
            ? new List<(SpaceTime? Placement, string ChildId)>()
            : family.BasicEntriesOf(previous).ToList();

        var subEntries = previous is null
            ? new List<(SpaceTime? Placement, string ChildId)>()
            : family.SubEntriesOf(previous).ToList();

        var newId = NextId(family.FullPrefix);

        if (containerIsBasic)
        {
            basicEntries.Add((null, containerAssetId));
            PutReference(newId, containerAssetId);
        }

        foreach (var (childId, placement) in children)
        {
            if (!storage.Exists(childId))
                throw new InvalidOperationException($"{childId} does not exist.");

            var childIsBasic = IsBasic(childId);
            var childIsFull  = IsFull(childId);

            if (!childIsBasic && !childIsFull)
                throw new InvalidOperationException($"{childId} is not a {family.BasicPrefix} or a {family.FullPrefix}.");

            if (childIsFull && containerIsFull && WouldCreateCycle(containerAssetId, childId))
                throw new InvalidOperationException($"Adding {childId} to {containerAssetId} would create a cycle.");

            PutReference(newId, childId);

            if (childIsBasic) basicEntries.Add((placement, childId));
            else              subEntries.Add((placement, childId));
        }

        storage.Put(newId, Serialize(family.Build(
            newId, previous, basicEntries, subEntries, AtTheOrigin(), listener)));

        return newId;
    }

    public string AddChild(string containerAssetId, string childAssetId, SpaceTime? placement = null) =>
        Compose(containerAssetId, new[] { (childAssetId, placement) });

    // Moves a child WITHIN its container, producing a new version of the
    // CONTAINER. The child is untouched: what changes is where the container
    // says it sits.
    public string MoveChild(string containerAssetId, string childAssetId, SpaceTime? placement)
    {
        if (!storage.Exists(containerAssetId))
            throw new InvalidOperationException($"{containerAssetId} does not exist.");

        if (!IsFull(containerAssetId))
            throw new InvalidOperationException($"{containerAssetId} is not a {family.FullPrefix}.");

        var previous = ReadFull(containerAssetId);

        var basicEntries = family.BasicEntriesOf(previous)
            .Select(e => e.ChildId == childAssetId ? (placement, e.ChildId) : e)
            .ToList();

        var subEntries = family.SubEntriesOf(previous)
            .Select(e => e.ChildId == childAssetId ? (placement, e.ChildId) : e)
            .ToList();

        var newId = NextId(family.FullPrefix);

        foreach (var referenced in References(containerAssetId))
            PutReference(newId, referenced);

        storage.Put(newId, Serialize(family.Build(
            newId, previous, basicEntries, subEntries, null, null)));

        return newId;
    }

    // ---- reading it back -----------------------------------------------

    // Resolves an Object's entries into content.
    //
    // A BASIC Object materialises as itself: an Object of one, so a caller asks
    // for the content of what it has without first asking which kind it is.
    public TFull Materialize(string assetId)
    {
        if (!storage.Exists(assetId))
            throw new InvalidOperationException($"{assetId} does not exist.");

        if (IsBasic(assetId))
            return family.FromBasic(assetId, ReadBasic(assetId));

        if (!IsFull(assetId))
            throw new InvalidOperationException($"{assetId} is not a {family.BasicPrefix} or a {family.FullPrefix}.");

        var stored = ReadFull(assetId);

        var basicChildren = new List<(SpaceTime? Placement, TBasic Child)>();
        var subChildren   = new List<(SpaceTime? Placement, TFull Child)>();

        foreach (var (placement, childId) in family.BasicEntriesOf(stored))
        {
            if (!storage.Exists(childId)) continue;
            basicChildren.Add((placement, ReadBasic(childId)));
        }

        foreach (var (placement, childId) in family.SubEntriesOf(stored))
        {
            if (!storage.Exists(childId)) continue;
            subChildren.Add((placement, Materialize(childId)));
        }

        return family.BuildResolved(assetId, stored, basicChildren, subChildren);
    }

    // ---- storing a new version of something ----------------------------

    // For an AIM's own editing operations, which are NOT shared: audio edits an
    // acoustic profile, speech edits a language. Each writes its own new version
    // through here, so the key minting and reference bookkeeping stay in one
    // place.
    public string StoreNewVersion(string ofAssetId, object updated, string prefix)
    {
        var newId = NextId(prefix);

        foreach (var referenced in References(ofAssetId))
            PutReference(newId, referenced);

        storage.Put(newId, Serialize(updated));
        return newId;
    }

    // ---- underneath ----------------------------------------------------

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

    private bool IsType(string assetId, string prefix) =>
        assetId.StartsWith(prefix, StringComparison.Ordinal);

    private bool WouldCreateCycle(string rootId, string candidateChildId)
    {
        if (rootId == candidateChildId) return true;

        var seen = new HashSet<string>();
        var pending = new Queue<string>();
        pending.Enqueue(candidateChildId);

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (!seen.Add(current)) continue;
            if (current == rootId) return true;

            foreach (var referenced in References(current))
                pending.Enqueue(referenced);
        }

        return false;
    }

    // A Space/Time at the origin, for a composed Object not yet placed.
    private static SpaceTime AtTheOrigin() => new()
    {
        SpatialAttitude1 = new SpatialAttitude
        {
            ObjectSpatialAttitudeID = Guid.NewGuid().ToString(),
            Position = new Position
            {
                PositionID   = Guid.NewGuid().ToString(),
                CartPosition = new double[] { 0, 0, 0 }
            }
        }
    };

    private static byte[] Serialize<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, Json);

    private static T Deserialize<T>(byte[] data) =>
        JsonSerializer.Deserialize<T>(data, Json)
        ?? throw new InvalidOperationException($"Could not read a {typeof(T).Name}.");
}