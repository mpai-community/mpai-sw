using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace AIF.Store;

// L3s FROM THE MPAI STORE - MPAI-MAS actions 8 and 9. An SCI names the Modules it
// serves; this fetches each Module's L3 from the Store, and the L3s of every
// Sub-AIM they contain, recursively, into a cache folder. The Controller then
// reads that folder exactly as it reads AIMs\AMDs: nothing downstream knows, or
// needs to know, where an L3 came from.
//
// The cache is kept. If the Store cannot be reached, an L3 already in the cache is
// used, and said to be; one neither in the Store nor in the cache is reported
// missing, and the Module that needs it will not load.
public static class StoreL3Source
{
    public sealed record Result(int Fetched, int FromCache, IReadOnlyList<string> Missing);

    public static async Task<Result> FetchAsync(string storeUrl, IEnumerable<string> modules, string cacheFolder, Action<string> say)
    {
        Directory.CreateDirectory(cacheFolder);
        using var http = new HttpClient { BaseAddress = new Uri(storeUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(15) };

        var queue = new Queue<string>(modules.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int fetched = 0, cached = 0;
        var missing = new List<string>();

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!seen.Add(id)) continue;
            var file = Path.Combine(cacheFolder, id + ".json");
            string? text = null;

            try
            {
                using var answer = await http.GetAsync($"MPAI/Store/L3/{Uri.EscapeDataString(id)}");
                if (answer.IsSuccessStatusCode)
                {
                    text = await answer.Content.ReadAsStringAsync();
                    await File.WriteAllTextAsync(file, text);
                    var version = answer.Headers.TryGetValues("MPAI-Store-Version", out var v) ? v.FirstOrDefault() : "?";
                    say($"  L3 {id,-22} v{version} from the Store");
                    fetched++;
                }
                else
                {
                    say($"  L3 {id,-22} NOT in the Store ({(int)answer.StatusCode})");
                    missing.Add(id);
                    continue;
                }
            }
            catch (Exception ex)
            {
                if (File.Exists(file))
                {
                    text = await File.ReadAllTextAsync(file);
                    say($"  L3 {id,-22} from the cache - the Store could not be reached ({ex.Message})");
                    cached++;
                }
                else
                {
                    say($"  L3 {id,-22} MISSING - the Store could not be reached and it is not in the cache");
                    missing.Add(id);
                    continue;
                }
            }

            // Its Sub-AIMs, which the Controller will need to build it.
            try
            {
                using var doc = JsonDocument.Parse(text!);
                if (doc.RootElement.TryGetProperty("SubAIMs", out var subs) && subs.ValueKind == JsonValueKind.Array)
                    foreach (var s in subs.EnumerateArray())
                        if (s.TryGetProperty("Identifier", out var i) && i.TryGetProperty("AIMName", out var n) &&
                            n.GetString() is { Length: > 0 } sub && sub != id)
                            queue.Enqueue(sub);
            }
            catch (JsonException) { say($"  L3 {id,-22} is not JSON: its Sub-AIMs cannot be followed"); }
        }
        return new Result(fetched, cached, missing);
    }
}
