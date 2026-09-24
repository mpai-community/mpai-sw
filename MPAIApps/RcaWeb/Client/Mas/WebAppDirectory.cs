using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Mpai.RcaWeb.Mas;

// WHAT A SERVICE OFFERS, asked from a browser. The same requests as
// Mpai.Rca.AppDirectory, through the page's own HttpClient: the desktop one
// builds a handler that accepts the development certificate, which a browser
// does not allow and does not need.
public sealed class WebAppDirectory
{
    public sealed record App(string Id, string Name, string Description, string? IconPath, string WorkflowPath, string Pane);

    // WHAT A PERSON NEEDS TO CHOOSE AN APP AND CONSENT TO IT.
    public sealed record Descriptor(string Id, string Name, string Description, string Standard, string Version,
                                    IReadOnlyList<string> Languages, IReadOnlyList<string> Asks, string Keeps);

    private readonly HttpClient http;
    // Where this client's Apps are: the Service's default offer, or one collection.
    private readonly string apps;

    public WebAppDirectory(HttpClient http, string? collection = null)
    {
        this.http = http;
        apps = "MPAI/AIFU/" + (string.IsNullOrWhiteSpace(collection) ? "" : $"c/{Uri.EscapeDataString(collection.Trim())}/");
    }

    public async Task<IReadOnlyList<App>> ListAsync() => Parse(await http.GetStringAsync($"{apps}Apps"));

    public async Task<IReadOnlyList<App>> SearchAsync(string? words, string? category) =>
        Parse(await http.GetStringAsync($"{apps}Apps?q={Uri.EscapeDataString(words ?? "")}&category={Uri.EscapeDataString(category ?? "")}"));

    public async Task<IReadOnlyList<string>> CategoriesAsync()
    {
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"{apps}Categories"));
            return doc.RootElement.EnumerateArray().Select(c => c.GetProperty("name").GetString() ?? "").Where(c => c.Length > 0).ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    public async Task<Descriptor?> DescriptorAsync(string appId)
    {
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"{apps}Apps/{Uri.EscapeDataString(appId)}/Descriptor"));
            var r = doc.RootElement;
            string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            IReadOnlyList<string> L(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : Array.Empty<string>();
            return new Descriptor(appId, S("Name"), S("Description"), S("Standard"), S("Version"), L("Languages"), L("Asks"), S("Keeps"));
        }
        catch { return null; }
    }

    private static IReadOnlyList<App> Parse(string json)
    {
        var apps = new List<App>();
        using var doc = JsonDocument.Parse(json);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            string S(string name) =>
                e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var icon = S("icon");
            apps.Add(new App(S("id"), S("name"), S("description"),
                             string.IsNullOrEmpty(icon) ? null : $"MPAI/AIFU/{icon}",
                             $"MPAI/AIFU/{S("workflow")}",
                             S("pane") is { Length: > 0 } w ? w : "normal"));
        }
        return apps;
    }

    public Task<string> WorkflowAsync(string appId) => http.GetStringAsync($"{apps}Apps/{appId}");

    // How many clients are using the Service now, this one included; null when the
    // Service does not say.
    public async Task<int?> ActiveClientsAsync()
    {
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync("MPAI/AIFU/Status"));
            return doc.RootElement.GetProperty("activeClients").GetInt32();
        }
        catch { return null; }
    }
}
