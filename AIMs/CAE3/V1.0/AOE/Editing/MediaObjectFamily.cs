using System.Collections.Generic;

using Mpai.Core;

namespace Mpai.Cae.Editing;

// WHAT A MEDIUM HAS TO SAY ABOUT ITSELF, so that COMPOSITION can be written
// once rather than per medium.
//
// Every medium has the same four things: a Basic Object, a full Object that
// holds others, and an entry placing each kind inside a full one. Audio, Speech
// and Text have them; Visual will.
//
// COMPOSITION IS THE SAME OPERATION on different types - putting a thing inside
// a thing at a position - so it is shared. EDITING IS NOT, and is deliberately
// absent from this interface: audio has an acoustic profile, speech has a
// language and speech descriptors, and changing the language of a speech Object
// means recognising, translating and re-synthesising it. Those are different
// operations that happen to overlap, not one operation on different types.
//
// Share what is provably the same; separating something wrongly shared is much
// harder than sharing something later.
public interface IMediaObjectFamily<TBasic, TFull>
    where TBasic : class
    where TFull : class
{
    // The Asset key prefixes: "BAO" and "AUO"; "BSO" and "SPO".
    string BasicPrefix { get; }
    string FullPrefix { get; }

    string IdOfBasic(TBasic basic);
    string IdOfFull(TFull full);

    TBasic WithId(TBasic basic, string id);

    // A stub carrying only an identifier - what an entry holds when a child is
    // referenced rather than embedded.
    TBasic BasicStub(string id);
    TFull FullStub(string id);

    // What a composed Object contains, as placement and child identifier.
    // Reading is uniform even though the two entry types are not.
    IReadOnlyList<(SpaceTime? Placement, string ChildId)> BasicEntriesOf(TFull full);
    IReadOnlyList<(SpaceTime? Placement, string ChildId)> SubEntriesOf(TFull full);

    // Build a composed Object from entries. previous is null when the container
    // was a Basic Object - the case that mints the first full Object.
    TFull Build(
        string id,
        TFull? previous,
        IReadOnlyList<(SpaceTime? Placement, string ChildId)> basicEntries,
        IReadOnlyList<(SpaceTime? Placement, string ChildId)> subEntries,
        SpaceTime? spaceTime,
        PointOfView? listener);

    // The same, with children resolved rather than referenced - for Materialize.
    TFull BuildResolved(
        string id,
        TFull? stored,
        IReadOnlyList<(SpaceTime? Placement, TBasic Child)> basicChildren,
        IReadOnlyList<(SpaceTime? Placement, TFull Child)> subChildren);

    // A Basic Object presented as an Object of one.
    TFull FromBasic(string id, TBasic basic);
}