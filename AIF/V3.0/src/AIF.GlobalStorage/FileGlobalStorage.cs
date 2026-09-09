using System.Text;
using System.Text.Json;

namespace AIF.GlobalStorage;

// A file-backed implementation of IGlobalStorage - one folder holds a
// ".data" file (the raw value) and a ".info" file (framework-stamped
// provenance) per key. Follows the same "root path layers on top of an
// existing folder rather than requiring an empty one" persistence approach
// already used by AssetRepository elsewhere in CAE-ASM.
public sealed class FileGlobalStorage : IGlobalStorage
{
    private readonly string rootPath;

    // Identifies the Top AIM this instance stamps into every Put, per
    // Section 2.4 of the proposal - "the framework's own knowledge of
    // which AIM is executing." CAE-ASM's reference software runs its AIMs
    // by direct, in-process calls rather than through an AIF Controller
    // (documented explicitly in CAE-ASM-V1.0's own AMD), so there is no
    // Controller supplying this automatically the way a genuine AIF
    // deployment would. Until CAE-ASM runs under a real Controller, the
    // caller constructing this instance supplies it explicitly - the
    // closest honest approximation available, and still gives every Put
    // through this instance the same, single, un-spoofable value, which is
    // what Section 2.4 actually requires (the caller of Put cannot supply
    // or override it - only the caller of this constructor can).
    private readonly string topAim;

    public FileGlobalStorage(string rootPath, string topAim)
    {
        this.rootPath = rootPath;
        this.topAim = topAim;
        Directory.CreateDirectory(rootPath);
    }

    public void Put(string key, byte[] data)
    {
        var (dataPath, infoPath) = PathsFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(dataPath)!);
        File.WriteAllBytes(dataPath, data);

        var info = new KeyInfo { StoredBy = topAim, StoredAt = DateTime.UtcNow };
        File.WriteAllText(infoPath, JsonSerializer.Serialize(info));
    }

    public byte[] Get(string key)
    {
        var (dataPath, _) = PathsFor(key);
        if (!File.Exists(dataPath))
            throw new KeyNotFoundException($"No value exists at key '{key}'.");

        return File.ReadAllBytes(dataPath);
    }

    public void Delete(string key)
    {
        var (dataPath, infoPath) = PathsFor(key);
        if (File.Exists(dataPath)) File.Delete(dataPath);
        if (File.Exists(infoPath)) File.Delete(infoPath);
    }

    public IReadOnlyList<string> List(string prefix)
    {
        if (!Directory.Exists(rootPath)) return Array.Empty<string>();

        return Directory.GetFiles(rootPath, "*.data", SearchOption.TopDirectoryOnly)
            .Select(KeyFromDataPath)
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
    }

    public bool Exists(string key)
    {
        var (dataPath, _) = PathsFor(key);
        return File.Exists(dataPath);
    }

    public KeyInfo GetKeyInfo(string key)
    {
        var (_, infoPath) = PathsFor(key);
        if (!File.Exists(infoPath))
            throw new KeyNotFoundException($"No value exists at key '{key}'.");

        return JsonSerializer.Deserialize<KeyInfo>(File.ReadAllText(infoPath))!;
    }

    // Keys are arbitrary strings (the Repository-pattern examples in the
    // proposal use ':' freely, e.g. "AudioObject:AUO000001:v3"), which are
    // not safe file names on every platform - encode rather than mapping
    // the key directly onto a path.
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