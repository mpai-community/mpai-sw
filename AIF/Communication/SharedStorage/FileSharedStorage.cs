using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace AIF.SharedStorage;

// A file-backed implementation of ISharedStorage: per key, a ".data" file (the
// raw value) and a ".info" file (framework-stamped provenance + length), in one
// flat folder. Writes are ATOMIC per key (Section 4.10.0): the value and its
// provenance are staged to temporary files and then swapped into place, so a
// concurrent reader never sees a new value paired with stale provenance, nor a
// torn value; and a crash mid-write leaves the previous consistent state, not a
// mismatched pair. A per-key lock serialises writers to the same key.
public sealed class FileSharedStorage : ISharedStorage
{
    private readonly string rootPath;
    private readonly string writer;       // Module, and the AIM within it, stamped into
                                          // every Put. The Controller binds this; a writer
                                          // cannot supply its own identity.
    private readonly string requestedBy;  // UA / RCA identity stamped into every Put (Section 2.5)

    // Per-key locks so two writers to the same key cannot interleave. Ordering
    // between different AIMs remains last-writer-wins, per the spec.
    private readonly ConcurrentDictionary<string, object> locks = new(StringComparer.Ordinal);
    private object LockFor(string key) => locks.GetOrAdd(key, _ => new object());

    public FileSharedStorage(string rootPath, string writer, string requestedBy)
    {
        this.rootPath = rootPath;
        this.writer = writer;
        this.requestedBy = requestedBy;
        Directory.CreateDirectory(rootPath);
    }

    public void MPAI_AIFM_SharedStorage_Put(string key, byte[] data)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("key must be non-empty", nameof(key));
        data ??= Array.Empty<byte>();
        var (dataPath, infoPath) = PathsFor(key);

        var info = new KeyInfo
        {
            StoredBy = writer, RequestedBy = requestedBy,
            StoredAt = DateTime.UtcNow, Length = data.LongLength
        };
        var infoJson = JsonSerializer.SerializeToUtf8Bytes(info);

        lock (LockFor(key))
        {
            // Stage both files first, then swap atomically so value+provenance
            // become visible together. Info is committed BEFORE data, so a reader
            // can never observe a new value with stale/absent provenance.
            var dTmp = dataPath + ".tmp";
            var iTmp = infoPath + ".tmp";
            File.WriteAllBytes(dTmp, data);
            File.WriteAllBytes(iTmp, infoJson);

            AtomicSwap(iTmp, infoPath);   // provenance in place first
            AtomicSwap(dTmp, dataPath);   // then the value
        }
    }

    // From an offset, atomic per key as a whole-value Put is: the read of the
    // existing value and the write of the new one hold the key's lock.
    public void MPAI_AIFM_SharedStorage_Put(string key, byte[] data, long offset)
    {
        if (offset == 0) { MPAI_AIFM_SharedStorage_Put(key, data); return; }
        lock (LockFor(key))
        {
            var (dataPath, _) = PathsFor(key);
            var existing = File.Exists(dataPath) ? File.ReadAllBytes(dataPath) : Array.Empty<byte>();
            MPAI_AIFM_SharedStorage_Put(key, SharedStorageRanges.Write(existing, data, offset));
        }
    }

    public byte[] MPAI_AIFM_SharedStorage_Get(string key, long offset, long length) =>
        SharedStorageRanges.Read(MPAI_AIFM_SharedStorage_Get(key), key, offset, length);

    public byte[] MPAI_AIFM_SharedStorage_Get(string key)
    {
        var (dataPath, _) = PathsFor(key);
        if (!File.Exists(dataPath))
            throw new KeyNotFoundException($"No value exists at key '{key}'.");
        return File.ReadAllBytes(dataPath);
    }

    public void MPAI_AIFM_SharedStorage_Delete(string key)
    {
        var (dataPath, infoPath) = PathsFor(key);
        lock (LockFor(key))
        {
            if (File.Exists(dataPath)) File.Delete(dataPath);
            if (File.Exists(infoPath)) File.Delete(infoPath);
        }
    }

    public IReadOnlyList<string> MPAI_AIFM_SharedStorage_List(string prefix)
    {
        if (!Directory.Exists(rootPath)) return Array.Empty<string>();
        return Directory.GetFiles(rootPath, "*.data", SearchOption.TopDirectoryOnly)
            .Select(KeyFromDataPath)
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
    }

    public bool MPAI_AIFM_SharedStorage_Exists(string key)
    {
        var (dataPath, _) = PathsFor(key);
        return File.Exists(dataPath);
    }

    public KeyInfo MPAI_AIFM_SharedStorage_GetKeyInfo(string key)
    {
        var (dataPath, infoPath) = PathsFor(key);
        if (!File.Exists(infoPath))
            throw new KeyNotFoundException($"No value exists at key '{key}'.");
        var info = JsonSerializer.Deserialize<KeyInfo>(File.ReadAllText(infoPath))!;
        // Length is authoritative from the value file, in case an older .info
        // predates the Length field or the two ever diverge.
        if (File.Exists(dataPath))
        {
            var len = new FileInfo(dataPath).Length;
            if (info.Length != len)
                info = new KeyInfo { StoredBy = info.StoredBy, RequestedBy = info.RequestedBy, StoredAt = info.StoredAt, Length = len };
        }
        return info;
    }

    // Atomically move src onto dest, replacing dest if present. File.Move with
    // overwrite is atomic on the same volume on .NET; fall back to Replace.
    private static void AtomicSwap(string src, string dest)
    {
        try { File.Move(src, dest, overwrite: true); }
        catch (IOException)
        {
            if (File.Exists(dest)) File.Replace(src, dest, null);
            else File.Move(src, dest);
        }
    }

    // Keys are arbitrary strings (the Repository examples use ':' freely, e.g.
    // "AudioObject:AUO000001:v3"), not safe file names everywhere - encode them.
    private (string dataPath, string infoPath) PathsFor(string key)
    {
        var safe = Convert.ToBase64String(Encoding.UTF8.GetBytes(key)).Replace('/', '_').Replace('+', '-');
        return (Path.Combine(rootPath, safe + ".data"), Path.Combine(rootPath, safe + ".info"));
    }

    private static string KeyFromDataPath(string dataPath)
    {
        var safe = Path.GetFileNameWithoutExtension(dataPath);
        var bytes = Convert.FromBase64String(safe.Replace('_', '/').Replace('-', '+'));
        return Encoding.UTF8.GetString(bytes);
    }
}
