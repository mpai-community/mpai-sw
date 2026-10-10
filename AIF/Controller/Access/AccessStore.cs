using System.Text;
using System.Text.Json;

namespace AIF.Controller;

// ACCESS (AIF V3.0, Storage 6): the static or slowly changing data a Module needs -
// domain knowledge, data models, knowledge bases - which the AIMs of a Module READ and
// never write. Its content is organised in Sources: a set of items, each at a Key, with
// a Version that changes whenever its content changes, and ONE Writer, whoever created
// the Source - a Provider (a third party holding the rights to the data, wanting to be
// its only provider), named by the trust anchor its link was admitted with, or the User.
//
// The store enforces the one rule: only the Writer of a Source writes it. What proves a
// Provider is checked before a call reaches this store (AccessProviderServer): the link,
// the signature of each request. What the store adds for a Provider is the Version: an
// update states the new Version, and one not higher than the current one is refused -
// an old item cannot be sent again, nor a Source brought back to an earlier state. A
// User's update raises the Version by one.
//
// On disk, per Source a folder (its name in hexadecimal, any text being a valid Source):
// source.json - the Source, its Writer and Version - and per item a .data and a .info
// (the Key, who wrote it, when, at which Version).
public enum AccessOutcome { OK, NotFound, NotAuthorised, Exists, Refused }

public sealed record AccessTrace(string Writer, DateTimeOffset Stamp, long Version);

public sealed class AccessStore : IAccessReader
{
    public const string User = "User";

    private readonly string root;
    private readonly Func<DateTimeOffset> now;
    private readonly object one = new();

    public AccessStore(string root, Func<DateTimeOffset>? now = null)
    {
        this.root = Path.GetFullPath(root);
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        Directory.CreateDirectory(this.root);
    }

    private sealed record SourceInfo(string Source, string Writer, long Version);
    private sealed record ItemInfo(string Key, string Writer, DateTimeOffset Stamp, long Version);

    private static string Hex(string text) => Convert.ToHexString(Encoding.UTF8.GetBytes(text));
    private string Folder(string source) => Path.Combine(root, Hex(source));
    private string Item(string source, string key) => Path.Combine(Folder(source), Hex(key));

    private SourceInfo? Info(string source)
    {
        var file = Path.Combine(Folder(source), "source.json");
        return File.Exists(file) ? JsonSerializer.Deserialize<SourceInfo>(File.ReadAllText(file)) : null;
    }

    private void Save(SourceInfo info) =>
        File.WriteAllText(Path.Combine(Folder(info.Source), "source.json"), JsonSerializer.Serialize(info));

    // ---- the Writer's: creating a Source and writing it --------------------------------

    public AccessOutcome Create(string source, string writer)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(writer)) return AccessOutcome.Refused;
        lock (one)
        {
            if (Info(source) is not null) return AccessOutcome.Exists;
            Directory.CreateDirectory(Folder(source));
            Save(new SourceInfo(source, writer, 0));
            return AccessOutcome.OK;
        }
    }

    // version: the new Version a Provider states; null for the User, whose update
    // raises the Version by one.
    public AccessOutcome Put(string source, string writer, string key, byte[] data, long? version = null) =>
        Write(source, writer, key, version, info =>
        {
            File.WriteAllBytes(Item(source, key) + ".data", data);
            File.WriteAllText(Item(source, key) + ".info", JsonSerializer.Serialize(new ItemInfo(key, writer, now(), info.Version)));
            return AccessOutcome.OK;
        });

    public AccessOutcome Delete(string source, string writer, string key, long? version = null) =>
        Write(source, writer, key, version, _ =>
        {
            if (!File.Exists(Item(source, key) + ".data")) return AccessOutcome.NotFound;
            File.Delete(Item(source, key) + ".data");
            File.Delete(Item(source, key) + ".info");
            return AccessOutcome.OK;
        });

    private AccessOutcome Write(string source, string writer, string key, long? version, Func<SourceInfo, AccessOutcome> act)
    {
        if (string.IsNullOrEmpty(key)) return AccessOutcome.Refused;
        lock (one)
        {
            if (Info(source) is not { } info) return AccessOutcome.NotFound;
            if (info.Writer != writer) return AccessOutcome.NotAuthorised;
            var next = version ?? info.Version + 1;
            if (next <= info.Version) return AccessOutcome.Refused;          // a replay, or a step back
            var outcome = act(info with { Version = next });
            if (outcome == AccessOutcome.OK) Save(info with { Version = next });
            return outcome;
        }
    }

    // ---- the AIMs': reading -------------------------------------------------------------

    public AccessOutcome Get(string source, string key, out byte[] data)
    {
        data = [];
        var file = Item(source, key) + ".data";
        if (!File.Exists(file)) return AccessOutcome.NotFound;
        data = File.ReadAllBytes(file);
        return AccessOutcome.OK;
    }

    public IReadOnlyList<string> List(string source, string prefix = "")
    {
        if (!Directory.Exists(Folder(source))) return [];
        return Directory.EnumerateFiles(Folder(source), "*.info")
            .Select(f => JsonSerializer.Deserialize<ItemInfo>(File.ReadAllText(f))!.Key)
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToList();
    }

    // The Version of a Source, -1 when there is no such Source.
    public long Version(string source) => Info(source)?.Version ?? -1;

    public string? WriterOf(string source) => Info(source)?.Writer;

    // Who wrote an item, when, and at which Version of its Source.
    public AccessOutcome Trace(string source, string key, out AccessTrace? trace)
    {
        trace = null;
        var file = Item(source, key) + ".info";
        if (!File.Exists(file)) return AccessOutcome.NotFound;
        var info = JsonSerializer.Deserialize<ItemInfo>(File.ReadAllText(file))!;
        trace = new AccessTrace(info.Writer, info.Stamp, info.Version);
        return AccessOutcome.OK;
    }
}
