using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Mpai.Mas.Server;

// WHAT THIS SERVICE OFFERS, AND TO WHOM. The catalogue holds the business's Apps;
// the offer says which of them each COLLECTION shows. A collection is a descriptor
// in the App repository listing some of the Apps - usually not all:
//
//     Apps/
//       Language/app.json   { "Name": ..., "Description": ..., "Apps": [ "MAT" ] }
//
// A Service serves several collections at once, each at its own address
// (/MPAI/AIFU/c/{collection}/...), and a default collection for an address that
// names none. With no collections configured, the default collection is exactly
// today's list of Apps, and every route answers as it always has.
//
// With a Store configured, an App is offered only if the Store has approved the L3
// of its Module; the Service says at start which it leaves out, and why.
public sealed class AppOffer
{
    public sealed record Collection(string Id, string Name, string Description, IReadOnlyList<AppCatalogue.Entry> Apps)
    {
        public AppCatalogue.Entry? Find(string id) =>
            Apps.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public AppCatalogue Catalogue { get; }
    public Collection Default { get; }
    private readonly Dictionary<string, Collection> named = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<Collection> Named => named.Values;

    private AppOffer(AppCatalogue catalogue, Collection byDefault) { Catalogue = catalogue; Default = byDefault; }

    public Collection? Find(string id) => named.TryGetValue(id, out var c) ? c : null;

    // TODAY'S CONFIGURATION: the catalogue's Apps are the default collection.
    public static AppOffer FromCatalogue(AppCatalogue catalogue) =>
        new(catalogue, new Collection("", "", "", catalogue.Apps.ToList()));

    public static async Task<AppOffer> BuildAsync(
        string? root, IEnumerable<string>? apps, string? shell,
        IEnumerable<string>? collections, string? defaultCollection, string? storeUrl,
        Action<string> say)
    {
        var plain = (apps ?? Array.Empty<string>()).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList();

        // The collections, from their descriptors.
        var defs = new List<(string Id, string Name, string Description, List<string> Members)>();
        foreach (var id in (collections ?? Array.Empty<string>()).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()))
        {
            var file = root is null ? null : Path.Combine(root, id, "app.json");
            if (file is null || !File.Exists(file)) { say($"  Collection '{id}': no descriptor at {file}."); continue; }
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                var members = r.TryGetProperty("Apps", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                    : new List<string>();
                if (members.Count == 0) { say($"  Collection '{id}': its descriptor lists no Apps."); continue; }
                defs.Add((id, r.TryGetProperty("Name", out var n) ? n.GetString() ?? id : id,
                              r.TryGetProperty("Description", out var d) ? d.GetString() ?? "" : "", members));
            }
            catch (Exception ex) { say($"  Collection '{id}': its descriptor will not parse ({ex.Message})."); }
        }

        // Everything any collection needs is held.
        var catalogue = AppCatalogue.Scan(root, plain.Concat(defs.SelectMany(c => c.Members)).Distinct(StringComparer.OrdinalIgnoreCase), shell);

        // THE STORE'S APPROVAL. With no Store configured, every held App may be offered.
        HashSet<string>? approved = null;
        if (!string.IsNullOrWhiteSpace(storeUrl))
        {
            approved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var http = new HttpClient { BaseAddress = new Uri(storeUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(10) };
            foreach (var app in catalogue.Held)
            {
                var isShell = string.Equals(app.Id, catalogue.ShellId, StringComparison.OrdinalIgnoreCase);
                string? why = null;
                if (app.Module is null) why = "names no Module";
                else
                {
                    try
                    {
                        using var answer = await http.GetAsync($"MPAI/Store/L3/{Uri.EscapeDataString(app.Module)}");
                        if (!answer.IsSuccessStatusCode) why = $"its Module {app.Module} is not approved by the Store";
                    }
                    catch (Exception ex) { why = $"the Store at {storeUrl} could not be asked ({ex.Message})"; }
                }
                if (why is null) approved.Add(app.Id);
                else say(isShell ? $"  Shell '{app.Id}': {why} - served all the same, since the clients need it."
                                 : $"  App '{app.Id}' not offered: {why}.");
            }
        }
        bool Offered(AppCatalogue.Entry e) => approved is null || approved.Contains(e.Id);
        List<AppCatalogue.Entry> Entries(IEnumerable<string> ids) =>
            ids.Select(catalogue.Find).Where(e => e is not null && Offered(e!)).Select(e => e!).ToList();

        var collectionsBuilt = defs.Select(c => new Collection(c.Id, c.Name, c.Description, Entries(c.Members))).ToList();
        var byDefault = defaultCollection is { Length: > 0 } dc && collectionsBuilt.FirstOrDefault(c => c.Id.Equals(dc, StringComparison.OrdinalIgnoreCase)) is { } chosen
            ? chosen
            : new Collection("", "", "", Entries(plain));
        if (defaultCollection is { Length: > 0 } && byDefault.Id.Length == 0)
            say($"  Default collection '{defaultCollection}' is not among the collections; the list of Apps is the default.");

        var offer = new AppOffer(catalogue, byDefault);
        foreach (var c in collectionsBuilt) offer.named[c.Id] = c;
        return offer;
    }

    // ---- what the routes answer --------------------------------------------------

    // The list exactly as it has always been answered: one form, one field order.
    public static string ListJson(IEnumerable<AppCatalogue.Entry> apps, string prefix) =>
        JsonSerializer.Serialize(
            apps.Select(a => new
            {
                id          = a.Id,
                name        = a.Name,
                description = a.Description,
                icon        = a.IconFile is null ? null : $"{prefix}Apps/{a.Id}/Icon",
                workflow    = $"{prefix}Apps/{a.Id}",
                pane        = a.Pane
            }),
            new JsonSerializerOptions { WriteIndented = true });

    // A search's answer: the same entries, each with its descriptor's findable fields.
    public static string ResultJson(IEnumerable<AppCatalogue.Entry> apps, string prefix)
    {
        var list = new JsonArray();
        foreach (var a in apps)
        {
            var d = Descriptor(a);
            list.Add(new JsonObject
            {
                ["id"] = a.Id, ["name"] = a.Name, ["description"] = a.Description,
                ["icon"] = a.IconFile is null ? null : $"{prefix}Apps/{a.Id}/Icon",
                ["workflow"] = $"{prefix}Apps/{a.Id}", ["pane"] = a.Pane,
                ["module"] = a.Module,
                ["keywords"] = d["Keywords"]?.DeepClone(), ["categories"] = d["Categories"]?.DeepClone(),
                ["asks"] = d["Asks"]?.DeepClone(), ["keeps"] = d["Keeps"]?.DeepClone()
            });
        }
        return list.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    // An App's whole descriptor, with its identifier and its routes.
    public static string DescriptorJson(AppCatalogue.Entry a, string prefix)
    {
        var d = Descriptor(a);
        d["Id"] = a.Id;
        d["Module"] ??= a.Module;
        d["IconRoute"] = a.IconFile is null ? null : $"{prefix}Apps/{a.Id}/Icon";
        d["WorkflowRoute"] = $"{prefix}Apps/{a.Id}";
        return d.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static string CategoriesJson(Collection c) =>
        JsonSerializer.Serialize(
            c.Apps.SelectMany(a => Strings(Descriptor(a)["Categories"]))
                  .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                  .OrderBy(g => g.Key)
                  .Select(g => new { name = g.Key, apps = g.Count() }),
            new JsonSerializerOptions { WriteIndented = true });

    public string CollectionsJson() =>
        JsonSerializer.Serialize(
            named.Values.Select(c => new { id = c.Id, name = c.Name, description = c.Description, apps = c.Apps.Count }),
            new JsonSerializerOptions { WriteIndented = true });

    // SEARCH. Every word of the query must be found in an App - in its name,
    // identifier, keywords, categories, languages or description - where a word is
    // found if it begins a word there or a word there begins it ("translate" finds
    // "translation"). Common words are ignored. Best matches first.
    public static IEnumerable<AppCatalogue.Entry> Search(Collection c, string? query, string? category)
    {
        var words = Words(query ?? "").Where(w => !Ignored.Contains(w)).ToList();
        var results = new List<(AppCatalogue.Entry App, int Score)>();
        foreach (var a in c.Apps)
        {
            var d = Descriptor(a);
            if (category is { Length: > 0 } &&
                !Strings(d["Categories"]).Any(x => x.Equals(category, StringComparison.OrdinalIgnoreCase)))
                continue;
            var fields = new (IEnumerable<string> Words, int Weight)[]
            {
                (Words(a.Name).Concat(Words(a.Id)), 3),
                (Strings(d["Keywords"]).SelectMany(Words), 2),
                (Strings(d["Categories"]).SelectMany(Words).Concat(Strings(d["UseCase"]).SelectMany(Words)), 2),
                (Strings(d["Languages"]).SelectMany(Words), 1),
                (Words(a.Description), 1)
            };
            var score = 0; var all = true;
            foreach (var w in words)
            {
                var best = fields.Where(f => f.Words.Any(x => Meets(w, x))).Select(f => f.Weight).DefaultIfEmpty(0).Max();
                if (best == 0) { all = false; break; }
                score += best;
            }
            if (all) results.Add((a, score));
        }
        return results.OrderByDescending(r => r.Score).ThenBy(r => r.App.Name).Select(r => r.App);
    }

    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "i", "me", "my", "to", "of", "in", "on", "for", "and", "or", "with", "what", "that",
        "want", "would", "like", "can", "you", "app", "something", "is", "it", "do", "say", "let"
    };

    private static bool Meets(string asked, string there) =>
        asked.Equals(there, StringComparison.OrdinalIgnoreCase) ||
        (asked.Length >= 4 && there.Length >= 4 &&
         (there.StartsWith(asked[..Math.Min(asked.Length, 6)], StringComparison.OrdinalIgnoreCase) ||
          asked.StartsWith(there[..Math.Min(there.Length, 6)], StringComparison.OrdinalIgnoreCase)));

    private static IEnumerable<string> Words(string text) =>
        text.Split(new[] { ' ', ',', '.', ';', ':', '-', '/', '(', ')', '!', '?', '\'', '"' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLowerInvariant());

    private static IEnumerable<string> Strings(JsonNode? node) =>
        node switch
        {
            JsonArray a => a.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0),
            JsonValue v when v.TryGetValue<string>(out var s) => new[] { s },
            _ => Array.Empty<string>()
        };

    private static JsonObject Descriptor(AppCatalogue.Entry a)
    {
        try { return JsonNode.Parse(a.Descriptor) as JsonObject ?? new JsonObject(); }
        catch { return new JsonObject(); }
    }
}
