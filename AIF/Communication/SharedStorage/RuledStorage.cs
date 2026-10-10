using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace AIF.SharedStorage;

// STORAGE UNDER RULES (M3219 3.1 to 3.3). The Private Storage of a Module, and a
// Shared Storage of one Module or several, hold each datum with the rules under
// which it may be read: who may read it and how long it is kept. The writer sets
// them, unless the store has a central control; with one, the general rules of
// each category are the central control's, and a writer may restrict them for
// its datum, never extend them. Nobody reaches a store but through a handle the
// Controller bound to its holder's identity.
//
// One layout on disk for both, the one Shared Storage has always had: per key a
// .data file and a .info file (KeyInfo), the rules in the .info beside the
// provenance. A .info without them is read as it always was: category Data,
// readable by every holder, kept as long as the scope.

// What a call to a store under rules comes to.
public enum StorageOutcome
{
    OK,
    NotFound,
    // Not allowed by the rules: a write the rules do not let this writer make, a
    // reader or a time beyond them, a read by one who is not a reader.
    NotAuthorised
}

// How long a datum is kept: until the Module instance that wrote it stops, until
// the session of the User Agent ends, or as long as the scope is kept - and,
// where given, no longer than a duration from its writing.
public enum StorageLifetime { Module, Session, Scope }

public sealed record StorageTime(StorageLifetime Lifetime = StorageLifetime.Scope, TimeSpan? Within = null)
{
    public static StorageTime Scope { get; } = new();

    // Not longer than other: a lifetime no longer, and a duration within other's.
    public bool NotLongerThan(StorageTime other) =>
        Lifetime <= other.Lifetime &&
        (other.Within is null || (Within is { } mine && mine <= other.Within.Value));

    // "Module", "Session", "Scope", "200ms", "5s", "Module 5s".
    public static StorageTime Parse(string text)
    {
        var lifetime = StorageLifetime.Scope;
        TimeSpan? within = null;
        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Enum.TryParse<StorageLifetime>(part, true, out var l)) lifetime = l;
            else if (part.EndsWith("ms") && double.TryParse(part[..^2], System.Globalization.CultureInfo.InvariantCulture, out var ms)) within = TimeSpan.FromMilliseconds(ms);
            else if (part.EndsWith('s') && double.TryParse(part[..^1], System.Globalization.CultureInfo.InvariantCulture, out var s)) within = TimeSpan.FromSeconds(s);
            else throw new FormatException($"'{part}' is not a lifetime or a duration.");
        }
        return new StorageTime(lifetime, within);
    }

    public override string ToString() => Lifetime + (Within is { } w ? $" {w.TotalMilliseconds:0.#}ms" : "");
}

// The general rules of a category, set by a central control: who may write it,
// who may read it, and the longest time. A name is a Module, an AIM Instance, or
// the User Agent.
public sealed record StorageRule(IReadOnlyCollection<string> Writers, IReadOnlyCollection<string> Readers, StorageTime Time);

// What is known of a datum: who wrote it and when, its category, its readers as
// the writer named them (none named: as the rules give), its time, and the rule
// under which the write was allowed.
public sealed record StorageTrace(string Writer, DateTimeOffset Stamp, string Category,
                                  IReadOnlyList<string>? Readers, StorageTime Time, string Rule);

// Who holds a handle: an AIM of a Module, the Controller for a Module, or the
// User Agent. A name in the rules matches the holder's AIM, its Module, or - for
// UserAgent - the User Agent.
public sealed record StorageHolder(string? Module, string Name)
{
    public static StorageHolder UserAgent { get; } = new(null, RuledStore.UserAgent);

    public bool Is(string name) => name == Name || (Module is not null && name == Module);

    public override string ToString() => Module is null ? Name : $"{Module}/{Name}";
}

// A handle: a store, as its holder may reach it.
public interface IRuledStorage
{
    string Holder { get; }
    StorageOutcome MPAI_AIFM_RuledStorage_Put(string key, byte[] data, string category,
                                              IReadOnlyCollection<string>? readers = null, StorageTime? time = null);
    StorageOutcome MPAI_AIFM_RuledStorage_Get(string key, out byte[] data);
    StorageOutcome MPAI_AIFM_RuledStorage_Delete(string key);
    IReadOnlyList<string> MPAI_AIFM_RuledStorage_List(string? category = null, string prefix = "");
    bool MPAI_AIFM_RuledStorage_Exists(string key);
    StorageOutcome MPAI_AIFM_RuledStorage_Trace(string key, out StorageTrace? trace);

    // The central control's: the general rules of a category.
    StorageOutcome MPAI_AIFM_RuledStorage_SetRule(string category, StorageRule rule);
}

// One store at one location.
public sealed class RuledStore
{
    public const string UserAgent = "UserAgent";
    public const string Controller = "Controller";
    public const string Default = "Data";

    // THE MODULE INSTANCES AND SESSIONS THAT ARE RUNNING, for data kept as long
    // as they run: a datum of one that is not running any more is gone.
    private static readonly ConcurrentDictionary<string, byte> running = new(StringComparer.Ordinal);
    public static void Started(string instanceOrSession) => running[instanceOrSession] = 0;
    public static void Ended(string instanceOrSession) => running.TryRemove(instanceOrSession, out _);

    // THE SHARED STORAGES, one per location, whichever Modules reach them.
    private static readonly ConcurrentDictionary<string, RuledStore> shared = new(StringComparer.OrdinalIgnoreCase);
    public static RuledStore SharedAt(string location, Func<DateTimeOffset> now) =>
        shared.GetOrAdd(Path.GetFullPath(location), l => new RuledStore(() => l, now, everyoneReads: true, centralControl: null));

    private readonly Func<string?> location;
    private readonly Func<DateTimeOffset> now;
    private readonly bool everyoneReads;
    private readonly Dictionary<string, StorageRule> rules = new(StringComparer.Ordinal);

    // HOW LONG, WHERE THE WRITER DOES NOT SAY (M3245 3.3): the deployment's time for a
    // category at this location; a writer may keep a datum for less, not for more.
    private readonly Dictionary<string, StorageTime> kept = new(StringComparer.Ordinal);
    public void Keep(string category, StorageTime time) { lock (one) kept[category] = time; }

    // WHAT HAS LAPSED, DELETED: of every Shared Storage, now - a session ended.
    public static void SweepAll()
    {
        foreach (var store in shared.Values) store.Sweep();
    }

    public int Sweep()
    {
        if (location() is not { } root || !Directory.Exists(root)) return 0;
        var gone = 0;
        lock (one)
            foreach (var info in Directory.EnumerateFiles(root, "*.info"))
                if (Load(KeyOf(info)) is null) gone++;
        return gone;
    }
    private readonly object one = new();

    // everyoneReads: a datum whose writer names no reader is read by every holder -
    // Shared Storage - or by its writer alone - Private Storage.
    public RuledStore(Func<string?> location, Func<DateTimeOffset> now, bool everyoneReads, StorageHolder? centralControl)
    {
        this.location = location;
        this.now = now;
        this.everyoneReads = everyoneReads;
        CentralControl = centralControl;
    }

    public StorageHolder? CentralControl { get; private set; }

    // A Shared Storage is given its central control when the User Agent names
    // the Module whose StorageControl holds it; one store, one central control.
    public bool Govern(StorageHolder control)
    {
        lock (one)
        {
            if (CentralControl is not null && CentralControl != control) return false;
            CentralControl = control;
            return true;
        }
    }

    // moduleInstance and session: those the holder's data of those lifetimes belong to.
    public IRuledStorage For(StorageHolder holder, string moduleInstance, string session) => new Handle(this, holder, moduleInstance, session);

    // The plain Shared Storage calls of M3203 4.10, on this store, for a holder:
    // what every AIM already does, now under the rules.
    public ISharedStorage PlainFor(StorageHolder holder, string moduleInstance, string session, string requestedBy) =>
        new Plain(new Handle(this, holder, moduleInstance, session), requestedBy);

    // ---- the files --------------------------------------------------------------

    private string Root()
    {
        var root = location() ?? throw new InvalidOperationException(
            "No storage is initialised for this Module (MPAI_AIFU_SharedStorage_Init).");
        Directory.CreateDirectory(root);
        return root;
    }

    private (string Data, string Info) PathsFor(string key)
    {
        var safe = Convert.ToBase64String(Encoding.UTF8.GetBytes(key)).Replace('/', '_').Replace('+', '-');
        var root = Root();
        return (Path.Combine(root, safe + ".data"), Path.Combine(root, safe + ".info"));
    }

    private static string KeyOf(string infoPath) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(Path.GetFileNameWithoutExtension(infoPath).Replace('_', '/').Replace('-', '+')));

    // A datum, unless there is none, or its time has passed - then it is removed.
    private KeyInfo? Load(string key)
    {
        var (dataPath, infoPath) = PathsFor(key);
        if (!File.Exists(infoPath) || !File.Exists(dataPath)) return null;
        var info = JsonSerializer.Deserialize<KeyInfo>(File.ReadAllBytes(infoPath))!;
        if (Expired(info)) { File.Delete(dataPath); File.Delete(infoPath); return null; }
        return info;
    }

    private bool Expired(KeyInfo info) =>
        (info.WithinMs is { } ms && now() - new DateTimeOffset(info.StoredAt, TimeSpan.Zero) > TimeSpan.FromMilliseconds(ms)) ||
        (info.Lifetime == StorageLifetime.Module && !running.ContainsKey(info.ModuleInstance ?? "")) ||
        (info.Lifetime == StorageLifetime.Session && !running.ContainsKey(info.Session ?? ""));

    // ---- the rules ----------------------------------------------------------------

    private StorageRule? RuleOf(string category)
    {
        lock (one) return rules.GetValueOrDefault(category);
    }

    private bool IsCentral(StorageHolder holder) => CentralControl is { } c && c == holder;

    private bool MayRead(StorageHolder holder, KeyInfo info)
    {
        if (holder.ToString() == info.StoredBy || IsCentral(holder)) return true;
        if (CentralControl is null)
            return info.Readers is { } named ? named.Any(holder.Is) : everyoneReads;

        // With a central control: a reader the general rules give now, and the
        // writer did not leave out.
        var rule = RuleOf(info.Category ?? Default);
        return rule is not null && rule.Readers.Any(holder.Is) && (info.Readers is null || info.Readers.Any(holder.Is));
    }

    private StorageOutcome Put(Handle by, string key, byte[] data, string category, IReadOnlyCollection<string>? readers, StorageTime? time, string requestedBy)
    {
        var holder = by.Holder;
        string ruleText;
        StorageTime? deployed;
        lock (one) deployed = kept.GetValueOrDefault(category);
        var effective = time is not null && (deployed is null || time.NotLongerThan(deployed)) ? time : deployed ?? time ?? StorageTime.Scope;
        if (CentralControl is null || IsCentral(holder) || holder.Name == Controller)
            ruleText = IsCentral(holder) ? "the central control's own" : deployed is not null ? "the deployment's" : "the writer's";
        else
        {
            var rule = RuleOf(category);
            if (rule is null || !rule.Writers.Any(holder.Is)) return StorageOutcome.NotAuthorised;
            if (readers is not null && readers.Any(r => !rule.Readers.Contains(r))) return StorageOutcome.NotAuthorised;
            if (time is not null && !time.NotLongerThan(rule.Time)) return StorageOutcome.NotAuthorised;
            effective = time ?? rule.Time;
            ruleText = $"the general rule of {category}";
        }

        lock (one)
        {
            // A datum another wrote is not overwritten, but by the central control
            // - or, in a Shared Storage, where it was written for every holder, as
            // Shared Storage has always been written.
            if (Load(key) is { } existing && existing.StoredBy != holder.ToString() && !IsCentral(holder) &&
                !(everyoneReads && existing.Readers is null && CentralControl is null))
                return StorageOutcome.NotAuthorised;

            var (dataPath, infoPath) = PathsFor(key);
            var info = new KeyInfo
            {
                StoredBy = holder.ToString(), RequestedBy = requestedBy, StoredAt = now().UtcDateTime, Length = data.LongLength,
                Category = category, Readers = readers?.ToList(), Lifetime = effective.Lifetime,
                WithinMs = effective.Within?.TotalMilliseconds, Rule = ruleText,
                ModuleInstance = by.ModuleInstance, Session = by.Session
            };
            File.WriteAllBytes(infoPath + ".tmp", JsonSerializer.SerializeToUtf8Bytes(info));
            File.WriteAllBytes(dataPath + ".tmp", data);
            File.Move(infoPath + ".tmp", infoPath, true);
            File.Move(dataPath + ".tmp", dataPath, true);
        }
        return StorageOutcome.OK;
    }

    private StorageOutcome Get(StorageHolder holder, string key, out byte[] data)
    {
        data = [];
        lock (one)
        {
            if (Load(key) is not { } info) return StorageOutcome.NotFound;
            if (!MayRead(holder, info)) return StorageOutcome.NotAuthorised;
            data = File.ReadAllBytes(PathsFor(key).Data);
            return StorageOutcome.OK;
        }
    }

    private StorageOutcome Delete(StorageHolder holder, string key)
    {
        lock (one)
        {
            if (Load(key) is not { } info) return StorageOutcome.NotFound;
            if (info.StoredBy != holder.ToString() && !IsCentral(holder) &&
                !(everyoneReads && info.Readers is null && CentralControl is null))
                return StorageOutcome.NotAuthorised;
            var (dataPath, infoPath) = PathsFor(key);
            File.Delete(infoPath);
            File.Delete(dataPath);
            return StorageOutcome.OK;
        }
    }

    private IReadOnlyList<string> List(StorageHolder holder, string? category, string prefix)
    {
        if (location() is not { } root || !Directory.Exists(root)) return [];
        lock (one)
            return Directory.EnumerateFiles(root, "*.info")
                .Select(KeyOf)
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .Where(k => Load(k) is { } i && (category is null || (i.Category ?? Default) == category) && MayRead(holder, i))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();
    }

    private StorageOutcome Trace(StorageHolder holder, string key, out StorageTrace? trace)
    {
        trace = null;
        lock (one)
        {
            if (Load(key) is not { } i) return StorageOutcome.NotFound;
            if (!MayRead(holder, i)) return StorageOutcome.NotAuthorised;
            trace = new StorageTrace(i.StoredBy, new DateTimeOffset(i.StoredAt, TimeSpan.Zero), i.Category ?? Default, i.Readers,
                                     new StorageTime(i.Lifetime, i.WithinMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null), i.Rule ?? "the writer's");
            return StorageOutcome.OK;
        }
    }

    private StorageOutcome SetRule(StorageHolder holder, string category, StorageRule rule)
    {
        if (!IsCentral(holder)) return StorageOutcome.NotAuthorised;
        lock (one) rules[category] = rule;
        return StorageOutcome.OK;
    }

    private sealed class Handle(RuledStore store, StorageHolder holder, string moduleInstance, string session) : IRuledStorage
    {
        public StorageHolder Holder { get; } = holder;
        public string ModuleInstance { get; } = moduleInstance;
        public string Session { get; } = session;
        string IRuledStorage.Holder => Holder.ToString();

        public StorageOutcome Put(string key, byte[] data, string category, IReadOnlyCollection<string>? readers, StorageTime? time, string requestedBy) =>
            store.Put(this, key, data, category, readers, time, requestedBy);

        public StorageOutcome MPAI_AIFM_RuledStorage_Put(string key, byte[] data, string category, IReadOnlyCollection<string>? readers = null, StorageTime? time = null) =>
            store.Put(this, key, data, category, readers, time, "local");
        public StorageOutcome MPAI_AIFM_RuledStorage_Get(string key, out byte[] data) => store.Get(Holder, key, out data);
        public StorageOutcome MPAI_AIFM_RuledStorage_Delete(string key) => store.Delete(Holder, key);
        public IReadOnlyList<string> MPAI_AIFM_RuledStorage_List(string? category = null, string prefix = "") => store.List(Holder, category, prefix);
        public bool MPAI_AIFM_RuledStorage_Exists(string key) => store.Get(Holder, key, out _) == StorageOutcome.OK;
        public StorageOutcome MPAI_AIFM_RuledStorage_Trace(string key, out StorageTrace? trace) => store.Trace(Holder, key, out trace);
        public StorageOutcome MPAI_AIFM_RuledStorage_SetRule(string category, StorageRule rule) => store.SetRule(Holder, category, rule);
    }

    // THE PLAIN CALLS, as every AIM makes them (M3203 4.10), and the rules beside
    // them: a Put names no reader - every holder reads it - and a read the rules
    // refuse is an UnauthorizedAccessException.
    private sealed class Plain(Handle handle, string requestedBy) : ISharedStorage, IRuledStorage
    {
        private static void Check(StorageOutcome outcome, string key)
        {
            if (outcome == StorageOutcome.NotFound) throw new KeyNotFoundException($"No value is stored at '{key}'.");
            if (outcome == StorageOutcome.NotAuthorised) throw new UnauthorizedAccessException($"'{key}' may not be reached by this AIM.");
        }

        public void MPAI_AIFM_SharedStorage_Put(string key, byte[] data)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("key must be non-empty", nameof(key));
            Check(handle.Put(key, data ?? [], Default, null, null, requestedBy), key);
        }

        public void MPAI_AIFM_SharedStorage_Put(string key, byte[] data, long offset)
        {
            var existing = handle.MPAI_AIFM_RuledStorage_Get(key, out var old) == StorageOutcome.OK ? old : [];
            MPAI_AIFM_SharedStorage_Put(key, offset == 0 ? data : SharedStorageRanges.Write(existing, data, offset));
        }

        public byte[] MPAI_AIFM_SharedStorage_Get(string key)
        {
            Check(handle.MPAI_AIFM_RuledStorage_Get(key, out var data), key);
            return data;
        }

        public byte[] MPAI_AIFM_SharedStorage_Get(string key, long offset, long length) =>
            SharedStorageRanges.Read(MPAI_AIFM_SharedStorage_Get(key), key, offset, length);

        public void MPAI_AIFM_SharedStorage_Delete(string key)
        {
            var outcome = handle.MPAI_AIFM_RuledStorage_Delete(key);
            if (outcome == StorageOutcome.NotAuthorised) Check(outcome, key);
        }

        public IReadOnlyList<string> MPAI_AIFM_SharedStorage_List(string prefix) => handle.MPAI_AIFM_RuledStorage_List(null, prefix);
        public bool MPAI_AIFM_SharedStorage_Exists(string key) => handle.MPAI_AIFM_RuledStorage_Exists(key);

        public KeyInfo MPAI_AIFM_SharedStorage_GetKeyInfo(string key)
        {
            Check(handle.MPAI_AIFM_RuledStorage_Trace(key, out var trace), key);
            handle.MPAI_AIFM_RuledStorage_Get(key, out var data);
            return new KeyInfo { StoredBy = trace!.Writer, RequestedBy = requestedBy, StoredAt = trace.Stamp.UtcDateTime, Length = data.LongLength };
        }

        public string Holder => handle.Holder.ToString();
        public StorageOutcome MPAI_AIFM_RuledStorage_Put(string key, byte[] data, string category, IReadOnlyCollection<string>? readers = null, StorageTime? time = null) =>
            handle.Put(key, data, category, readers, time, requestedBy);
        public StorageOutcome MPAI_AIFM_RuledStorage_Get(string key, out byte[] data) => handle.MPAI_AIFM_RuledStorage_Get(key, out data);
        public StorageOutcome MPAI_AIFM_RuledStorage_Delete(string key) => handle.MPAI_AIFM_RuledStorage_Delete(key);
        public IReadOnlyList<string> MPAI_AIFM_RuledStorage_List(string? category = null, string prefix = "") => handle.MPAI_AIFM_RuledStorage_List(category, prefix);
        public bool MPAI_AIFM_RuledStorage_Exists(string key) => handle.MPAI_AIFM_RuledStorage_Exists(key);
        public StorageOutcome MPAI_AIFM_RuledStorage_Trace(string key, out StorageTrace? trace) => handle.MPAI_AIFM_RuledStorage_Trace(key, out trace);
        public StorageOutcome MPAI_AIFM_RuledStorage_SetRule(string category, StorageRule rule) => handle.MPAI_AIFM_RuledStorage_SetRule(category, rule);
    }
}
