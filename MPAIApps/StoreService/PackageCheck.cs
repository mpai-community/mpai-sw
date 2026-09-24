using System.Text.Json;

namespace Mpai.StoreService;

// THE PACKAGE AN L3 NAMES. Each Implementations entry gives an ImplementationURI -
// where the Implementer uploaded the package - with its BinaryName, Architecture
// and OperatingSystem. This checks that the package is THERE; whether it is
// LEGITIMATE is the Store's later role, with fingerprints (action 14), not now.
//
// A composite AIM (one with Sub-AIMs) is run by the Controller from its Topology.
// It may have a package of its own, bundling the implementations of some of its
// Sub-AIMs; such a package carries the L3 of each AIM it bundles (see L3sIn).
//
// THE STORE LOOKS ONLY WHERE IT IS TOLD PACKAGES ARE. A file: URI is inspected only
// inside the packages folder (--Packages); one pointing elsewhere is signalled and
// not followed, so a submitted L3 cannot make the Store read its own disk.
public sealed class PackageCheck
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly string? packagesRoot;

    public PackageCheck(string? packagesRoot) =>
        this.packagesRoot = packagesRoot is null ? null : Path.GetFullPath(packagesRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    public string Where => packagesRoot ?? "(none: file: packages are not inspected)";

    // A local folder the Store may look into, or null.
    private string? Inspectable(Uri u)
    {
        if (!u.IsFile || packagesRoot is null) return null;
        var full = Path.GetFullPath(u.LocalPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(packagesRoot, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    public async Task<List<Finding>> CheckAsync(JsonElement l3)
    {
        var found = new List<Finding>();
        var composite = l3.TryGetProperty("SubAIMs", out var subs) && subs.ValueKind == JsonValueKind.Array && subs.GetArrayLength() > 0;

        if (!l3.TryGetProperty("Implementations", out var impls) || impls.ValueKind != JsonValueKind.Array || impls.GetArrayLength() == 0)
        {
            if (!composite) found.Add(new Finding("Package", "error", "No Implementations entry: nothing says where this AIM's package is."));
            return found;
        }

        foreach (var impl in impls.EnumerateArray())
        {
            string S(string name) => impl.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var uri = S("ImplementationURI"); var binary = S("BinaryName");
            var where = $"{S("Architecture")}/{S("OperatingSystem")}";

            if (uri.Length == 0) { if (!composite) found.Add(new Finding("Package", "error", $"{where}: ImplementationURI is empty.")); continue; }
            if (binary.Length == 0) found.Add(new Finding("Package", "warning", $"{where}: BinaryName is empty."));
            if (S("Architecture").Length == 0 || S("OperatingSystem").Length == 0)
                found.Add(new Finding("Package", "warning", $"{uri}: Architecture or OperatingSystem is not stated."));

            if (!Uri.TryCreate(uri, UriKind.Absolute, out var u))
            {
                found.Add(new Finding("Package", "error", $"'{uri}' is not an absolute URI."));
                continue;
            }
            if (u.IsFile)
            {
                var path = Inspectable(u);
                if (path is null)
                    found.Add(new Finding("Package", "warning", $"{uri}: outside the Store's packages folder, not inspected."));
                else if (Directory.Exists(path))
                {
                    if (!composite && binary.Length > 0 && !File.Exists(Path.Combine(path, binary + ".dll")))
                        found.Add(new Finding("Package", "error", $"{uri}: the folder is there, but {binary}.dll is not in it."));
                }
                else if (!File.Exists(path.TrimEnd(Path.DirectorySeparatorChar)))
                    found.Add(new Finding("Package", "error", $"{uri}: no package there."));
            }
            else if (u.Scheme is "http" or "https")
            {
                try
                {
                    using var head = new HttpRequestMessage(HttpMethod.Head, u);
                    using var answer = await Http.SendAsync(head);
                    if (!answer.IsSuccessStatusCode)
                        found.Add(new Finding("Package", "error", $"{uri}: answered {(int)answer.StatusCode}."));
                }
                catch (Exception ex) { found.Add(new Finding("Package", "error", $"{uri}: could not be reached ({ex.Message}).")); }
            }
            else found.Add(new Finding("Package", "warning", $"{uri}: a '{u.Scheme}' URI cannot be checked."));
        }
        return found;
    }

    // THE L3s A PACKAGE CARRIES - the AIMs it bundles. A package in the packages
    // folder is read, at its top level only, for *.json files whose
    // Identifier.AIMName names an AIM; a package anywhere else cannot be looked
    // into, and bundles nothing as far as the Store can tell.
    public HashSet<string> L3sIn(JsonElement l3)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (!l3.TryGetProperty("Implementations", out var impls) || impls.ValueKind != JsonValueKind.Array) return ids;
        foreach (var impl in impls.EnumerateArray())
        {
            if (!impl.TryGetProperty("ImplementationURI", out var u) || u.GetString() is not { Length: > 0 } uri) continue;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || Inspectable(parsed) is not { } folder || !Directory.Exists(folder)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly).ToList(); }
            catch (Exception) { continue; }
            foreach (var file in files)
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    if (doc.RootElement.TryGetProperty("Identifier", out var i) && i.TryGetProperty("AIMName", out var n) && n.GetString() is { Length: > 0 } id)
                        ids.Add(id);
                }
                catch { /* not an L3 */ }
            }
        }
        return ids;
    }
}
