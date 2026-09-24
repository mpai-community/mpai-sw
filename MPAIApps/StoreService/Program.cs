using System.Text.Json;
using Mpai.StoreService;

// THE MPAI STORE SERVICE.
//
//   POST /MPAI/Store/L3                      submit an L3: published with what was found, or refused
//   GET  /MPAI/Store/L3?name=...             approved L3s, latest versions (action 8)
//   GET  /MPAI/Store/L3/{id}[?version=n]     one L3 (action 9)
//   GET  /MPAI/Store/L3/{id}/versions        its published versions
//   GET  /MPAI/Store/L3/{id}/findings[?version=n]   what the Store found in it
//
//   dotnet run --project MPAIApps\StoreService -- --Urls https://localhost:5020 --Root <folder> --Packages <folder>
//
// --Root is where the Store keeps its L3s (default: <local application data>\MPAI\Store).
// --Schemas is the published schemas folder (default: the "schemas" folder above this program).
// --Packages is the folder holding all packages; file: URIs are inspected only inside it.

var builder = WebApplication.CreateBuilder(args);
var urls    = builder.Configuration["Urls"] ?? "https://localhost:5020";
builder.WebHost.UseUrls(urls);
var root    = builder.Configuration["Root"]
              ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MPAI", "Store");
var schemas = builder.Configuration["Schemas"] ?? FindSchemas() ?? "schemas";

var repository = new L3Repository(root);
var l2 = new L2Check(schemas);
var packages = new PackageCheck(builder.Configuration["Packages"]);
var app = builder.Build();

app.MapPost("/MPAI/Store/L3", async (HttpRequest request) =>
{
    var text = await new StreamReader(request.Body).ReadToEndAsync();
    JsonDocument document;
    try { document = JsonDocument.Parse(text); }
    catch (JsonException ex) { return Results.BadRequest(new { refused = "Not JSON: " + ex.Message }); }
    using (document)
    {
        var l3 = document.RootElement;
        if (l3.ValueKind != JsonValueKind.Object || !l3.TryGetProperty("Identifier", out var identifier) ||
            !identifier.TryGetProperty("AIMName", out var nameElement) || nameElement.GetString() is not { Length: > 0 } id)
            return Results.BadRequest(new { refused = "An L3 needs Identifier.AIMName: without it the Store cannot say what it is." });
        if (id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains(".."))
            return Results.BadRequest(new { refused = $"'{id}' cannot name an L3 in the Store." });

        // VALIDATION SIGNALS: against the L2, and for the package the L3 names.
        var findings = new List<Finding>();
        findings.AddRange(l2.Check(id, l3));
        findings.AddRange(await packages.CheckAsync(l3));

        // A MISSING SUB-AIM REFUSES: each Sub-AIM's L3 must be in the composite's
        // package or in the Store, or the composite could not be built.
        var bundled = packages.L3sIn(l3);
        var missing = new List<string>();
        if (l3.TryGetProperty("SubAIMs", out var subs) && subs.ValueKind == JsonValueKind.Array)
            foreach (var sub in subs.EnumerateArray())
                if (sub.TryGetProperty("Identifier", out var si) && si.TryGetProperty("AIMName", out var sn) &&
                    sn.GetString() is { Length: > 0 } subId && subId != id &&
                    !bundled.Contains(subId) && repository.Text(subId, null) is null)
                    missing.Add(subId);
        if (missing.Count > 0)
        {
            Console.WriteLine($"[Store] refused {id}: Sub-AIM(s) {string.Join(", ", missing)} in neither its package nor the Store");
            return Results.Json(new
            {
                id, published = false,
                refused = $"The L3 of {string.Join(", ", missing)} is in neither this AIM's package nor the Store. Submit it first, or bundle it in the package.",
                missing, findings
            }, statusCode: 422);
        }

        var entry = repository.Publish(id, text, findings);
        Console.WriteLine($"[Store] published {id} v{entry.Version}: {entry.Errors} error(s), {entry.Warnings} warning(s)");
        return Results.Json(new { id, version = entry.Version, published = true, findings }, statusCode: 201);
    }
});

app.MapGet("/MPAI/Store/L3", (string? name) => Results.Json(repository.Latest(name)));

app.MapGet("/MPAI/Store/L3/{id}", (HttpResponse response, string id, int? version) =>
{
    var text = repository.Text(id, version);
    if (text is null) return Results.NotFound(new { missing = $"{id}{(version is null ? "" : " v" + version)} is not in the Store." });
    response.Headers["MPAI-Store-Version"] = (version ?? repository.Versions(id).Last().Version).ToString();
    return Results.Text(text, "application/json");
});

app.MapGet("/MPAI/Store/L3/{id}/versions", (string id) =>
{
    var list = repository.Versions(id);
    return list.Count == 0 ? Results.NotFound(new { missing = $"{id} is not in the Store." }) : Results.Json(list);
});

app.MapGet("/MPAI/Store/L3/{id}/findings", (string id, int? version) =>
{
    var findings = repository.Findings(id, version);
    return findings is null ? Results.NotFound(new { missing = $"{id} is not in the Store." }) : Results.Json(findings);
});

Console.WriteLine("=== MPAI Store ===");
Console.WriteLine($"  Listen:   {urls}");
Console.WriteLine($"  Root:     {repository.Root}");
Console.WriteLine($"  Schemas:  {schemas}   ({l2.Count} L2s)");
Console.WriteLine($"  Packages: {packages.Where}");
Console.WriteLine($"  L3s:      {repository.Count}");
app.Run();

static string? FindSchemas()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (Directory.Exists(Path.Combine(dir.FullName, "schemas", "MMC")))
            return Path.Combine(dir.FullName, "schemas");
    return null;
}
