namespace AIF.SharedStorage;

// Framework-stamped provenance and size for a key, per MPAI-AIF V3.0 Shared
// Storage API Section 4.10.0/4.10.6: none of StoredBy/RequestedBy/StoredAt is
// ever supplied by a caller.
//   - StoredBy:    the Top AIM (the Composite AIM the caller executes under)
//                  that performed the most recent Put.
//   - RequestedBy: the identity of the User Agent (local) or Remote Client
//                  Application (a MAS-context RCA is "a remote UA") that the
//                  Controller already knows the caller as.
//   - StoredAt:    when the most recent Put occurred.
//   - Length:      the current byte length of the value, so a caller can size a
//                  Get (or a ranged read) without first transferring the value.
public sealed class KeyInfo
{
    public required string   StoredBy    { get; init; }
    public required string   RequestedBy { get; init; }
    public required DateTime StoredAt    { get; init; }
    public required long     Length      { get; init; }
}

// The six primitives of MPAI-AIF V3.0 Shared Storage API Section 4.10 -
// deliberately minimal: no type system, no forced versioning, no forced
// relationships. Anything richer (typed instances, versioning, references) is a
// convention built on these six (chiefly prefixed keys + List), not a separate
// facility.
//
// Values are whole-value: a value is written and read as a whole (the earlier
// draft's per-call offset is removed; ranged access is a reserved extension,
// Section 4.10.7). This interface represents ONE storage scope - one Module
// instance's Shared Storage, or one AIM's Private Storage; a multi-Module host
// constructs one instance per scope, so no Module/AIM identifier is passed per call.
public interface ISharedStorage
{
    // Stores data as the whole value at key, replacing any existing value. The
    // framework (this implementation) stamps StoredBy/RequestedBy/StoredAt
    // automatically - no parameter lets a caller supply or override them, which
    // is what makes GetKeyInfo trustworthy under a zero-trust model. The write
    // is atomic per key: the value and its provenance become visible together.
    void Put(string key, byte[] data);

    // Retrieves the whole value stored at key. Throws KeyNotFoundException if no
    // value exists at key (Section 4.10.2).
    byte[] Get(string key);

    // Removes the value stored at key, together with its provenance, if any.
    // Deleting a key that does not exist is not an error (Section 4.10.3).
    void Delete(string key);

    // Returns every currently stored key that begins with prefix (an empty
    // prefix matches every key), in ordinal order. The only enumeration
    // primitive - every richer query is a List with a suitable prefix.
    IReadOnlyList<string> List(string prefix);

    // True if a value is currently stored at key, without transferring its
    // content (Section 4.10.5).
    bool Exists(string key);

    // Retrieves the framework-stamped provenance and size of the most recent
    // Put to key (Section 4.10.6). Throws KeyNotFoundException if no value
    // exists at key.
    KeyInfo GetKeyInfo(string key);
}
