using AIF.SharedStorage;

namespace AIF.Controller;

// Supplies an implementation for an AIM named in the Metadata.
//
// The framework stays type-agnostic: it asks for an AIM by its standard name
// and hands over that AIM's settings; the provider - which lives with the
// implementations - decides what to construct.
//
// AND HANDS OVER THE STORAGE, ALREADY STAMPED. A provider that constructed its
// own storage chose what identity every write would carry, and the identity is
// the whole point: if data is written it must be possible to know who wrote it.
// An identity the writer supplies proves nothing. So the Controller binds a
// handle to the AIM it is about to instantiate, and the provider can pass it on
// but cannot forge it.
//
// The handle may be null where no storage scope is configured.
public interface IAimProvider
{
    // ASKED BEFORE BUILDING. A Service composed of several providers must know
    // which of them can make an AIM before it tries; and a Service told to offer
    // an App should say at startup that it cannot build one of its AIMs, rather
    // than failing when a person chooses it.
    //
    // The default says yes, so that a provider written before this question was
    // asked behaves as it always did.
    bool CanCreate(string aimName) => true;

    // WHICH BINARY IMPLEMENTS AN AIM (M3223 3.2): the file the provider would build it
    // from, so that it can be measured before it runs. Null where the provider cannot
    // say - a Controller that verifies what it runs then refuses the AIM.
    string? ImplementationOf(string aimName) => null;

    IAimProcessor Create(
        string aimName,
        IReadOnlyDictionary<string, string> settings,
        ISharedStorage? storage);

    // With the AIM's Private Storage as well (M3215 3.7), reachable by it alone.
    // A provider whose AIMs keep private data overrides this; the default hands
    // over the Shared handle only, as before.
    IAimProcessor Create(
        string aimName,
        IReadOnlyDictionary<string, string> settings,
        ISharedStorage? storage,
        ISharedStorage? privateStorage) =>
        Create(aimName, settings, storage);

    // With the Private Storage of its Module as well (M3219 3.1), reached under
    // the rules of its writers or its central control. A provider whose AIMs keep
    // data of the Module overrides this; the default hands over the others.
    IAimProcessor Create(
        string aimName,
        IReadOnlyDictionary<string, string> settings,
        ISharedStorage? storage,
        ISharedStorage? privateStorage,
        IRuledStorage? moduleStorage) =>
        Create(aimName, settings, storage, privateStorage);
}