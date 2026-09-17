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
    IAimProcessor Create(
        string aimName,
        IReadOnlyDictionary<string, string> settings,
        ISharedStorage? storage);
}