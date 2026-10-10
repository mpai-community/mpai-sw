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

    // THE RULES OF THE DATUM (M3219 3.1), where it was written under them; absent
    // in what was written before, which is read as category Data, every holder a
    // reader, kept as long as the scope.
    public string?          Category       { get; init; }
    public List<string>?    Readers        { get; init; }
    public StorageLifetime  Lifetime       { get; init; } = StorageLifetime.Scope;
    public double?          WithinMs       { get; init; }
    public string?          Rule           { get; init; }
    public string?          ModuleInstance { get; init; }
    public string?          Session        { get; init; }
}

// The six primitives of MPAI-AIF V3.0 Shared Storage API Section 4.10 -
// deliberately minimal: no type system, no forced versioning, no forced
// relationships. Anything richer (typed instances, versioning, references) is a
// convention built on these six (chiefly prefixed keys + List), not a separate
// facility.
//
// Values are written and read from an offset (M3203 4.10.1, 4.10.2; M3213 3.6):
// a Put at offset 0 replaces the whole value, a Put beyond the end zero-fills
// the gap, and a Get beyond the end is an error. The whole-value Put and Get are
// the same calls at offset 0. This interface represents ONE storage scope - one
// Module instance's Shared Storage, or one AIM's Private Storage; a multi-Module
// host constructs one instance per scope, so no Module/AIM identifier is passed
// per call.
public interface ISharedStorage
{
    // Stores data as the whole value at key, replacing any existing value. The
    // framework (this implementation) stamps StoredBy/RequestedBy/StoredAt
    // automatically - no parameter lets a caller supply or override them, which
    // is what makes GetKeyInfo trustworthy under a zero-trust model. The write
    // is atomic per key: the value and its provenance become visible together.
    void MPAI_AIFM_SharedStorage_Put(string key, byte[] data);

    // Retrieves the whole value stored at key. Throws KeyNotFoundException if no
    // value exists at key (Section 4.10.2).
    byte[] MPAI_AIFM_SharedStorage_Get(string key);

    // Removes the value stored at key, together with its provenance, if any.
    // Deleting a key that does not exist is not an error (Section 4.10.3).
    void MPAI_AIFM_SharedStorage_Delete(string key);

    // Returns every currently stored key that begins with prefix (an empty
    // prefix matches every key), in ordinal order. The only enumeration
    // primitive - every richer query is a List with a suitable prefix.
    IReadOnlyList<string> MPAI_AIFM_SharedStorage_List(string prefix);

    // True if a value is currently stored at key, without transferring its
    // content (Section 4.10.5).
    bool MPAI_AIFM_SharedStorage_Exists(string key);

    // Retrieves the framework-stamped provenance and size of the most recent
    // Put to key (Section 4.10.6). Throws KeyNotFoundException if no value
    // exists at key.
    KeyInfo MPAI_AIFM_SharedStorage_GetKeyInfo(string key);

    // MPAI_AIFM_SharedStorage_Put from an offset. At 0 the value is replaced
    // whole, and any longer existing value is discarded beyond data; beyond the
    // current end the gap is zero-filled; otherwise data overwrites that range and
    // the rest of the value remains. A key that does not exist is created.
    //
    // The default is made of the whole-value calls, and is atomic only as far as
    // they are; an implementation that can do better overrides it.
    void MPAI_AIFM_SharedStorage_Put(string key, byte[] data, long offset)
    {
        if (offset == 0) { MPAI_AIFM_SharedStorage_Put(key, data); return; }
        var existing = MPAI_AIFM_SharedStorage_Exists(key) ? MPAI_AIFM_SharedStorage_Get(key) : Array.Empty<byte>();
        MPAI_AIFM_SharedStorage_Put(key, SharedStorageRanges.Write(existing, data, offset));
    }

    // MPAI_AIFM_SharedStorage_Get from an offset, at most length bytes (fewer
    // where the value ends first). An offset beyond the end is an error.
    byte[] MPAI_AIFM_SharedStorage_Get(string key, long offset, long length) =>
        SharedStorageRanges.Read(MPAI_AIFM_SharedStorage_Get(key), key, offset, length);
}

// The offset semantics of M3203 4.10.1 and 4.10.2, in one place.
public static class SharedStorageRanges
{
    public static byte[] Write(byte[] existing, byte[] data, long offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        data ??= Array.Empty<byte>();
        if (offset == 0) return data;
        var value = new byte[Math.Max(existing.LongLength, offset + data.LongLength)];
        Array.Copy(existing, value, existing.LongLength);
        Array.Copy(data, 0, value, offset, data.LongLength);
        return value;
    }

    public static byte[] Read(byte[] value, string key, long offset, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (offset > value.LongLength)
            throw new ArgumentOutOfRangeException(nameof(offset),
                $"Offset {offset} is beyond the end of the value at key '{key}' ({value.LongLength} bytes).");
        var count = Math.Min(length, value.LongLength - offset);
        var part = new byte[count];
        Array.Copy(value, offset, part, 0, count);
        return part;
    }
}
