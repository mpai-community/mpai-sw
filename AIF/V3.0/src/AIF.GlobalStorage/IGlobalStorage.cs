namespace AIF.GlobalStorage;

// Framework-stamped provenance for a key, per Section 2.4 of the proposed
// MPAI-AIF V3.0 Global Storage API: neither field is ever supplied by a
// caller. StoredBy identifies the Top AIM (the Composite AIM the caller is
// executing under) that performed the most recent Put; StoredAt is when.
public sealed class KeyInfo
{
    public required string StoredBy { get; init; }
    public required DateTime StoredAt { get; init; }
}

// The six primitives proposed as new MPAI-AIF V3.0 Basic API Section 4.10 -
// deliberately minimal (Section 2.1-2.2 of the proposal): no type system,
// no forced versioning, no forced relationships. Anything richer (typed
// instances, versioning, references - see the proposal's Section 4) is a
// convention built using these six, not a separate facility.
//
// This interface deliberately omits the MODULE_ID/AIM_ID parameter that
// appears in the C-style proposal's function signatures: one
// IGlobalStorage instance represents one storage scope (one Module's Shared
// Storage, or one AIM's private Storage), matching CAE-ASM's own
// single-Module usage. A multi-Module host would construct one instance per Module.
public interface IGlobalStorage
{
    // Stores data at key, overwriting any existing value. The framework
    // (this implementation) stamps StoredBy/StoredAt automatically - there
    // is no parameter here for a caller to supply or override either,
    // which is what makes GetKeyInfo's result trustworthy under a
    // zero-trust model (Section 2.4 of the proposal).
    void Put(string key, byte[] data);

    // Retrieves the value stored at key. Throws KeyNotFoundException if no
    // value exists at key, matching the proposal's "returns an error"
    // (Section 4.10.2).
    byte[] Get(string key);

    // Removes the value stored at key, if any. Deleting a key that does
    // not exist is not an error (Section 4.10.3).
    void Delete(string key);

    // Returns every currently stored key that begins with prefix (an empty
    // prefix matches every key), in ordinal order. The only enumeration
    // primitive - every richer query (by type, by version, by reference;
    // see the proposal's Section 4) is expressed as a List call with a
    // suitable prefix, not a separate operation.
    IReadOnlyList<string> List(string prefix);

    // True if a value is currently stored at key, without transferring its
    // content (Section 4.10.5).
    bool Exists(string key);

    // Retrieves the framework-stamped provenance of the most recent Put to
    // key (Section 4.10.6). Throws KeyNotFoundException if no value exists
    // at key.
    KeyInfo GetKeyInfo(string key);
}