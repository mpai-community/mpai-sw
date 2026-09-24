using System;
using System.Linq;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Mpai.Rca;

// WHAT A SERVICE OFFERS, ASKED OVER MPAI-MAS.
//
// A client holds no application. It arrives knowing how to render an avatar,
// capture speech and interpret a Workflow Description, asks a Service what it
// has, and runs what the person chooses. Which application it is running is
// decided by a document, at the moment of choosing - not by what was compiled
// into it.
//
// This asks; it does not interpret. The workflow comes back as text and goes to
// the reader, which is what keeps the choice of application out of the client's
// code entirely.
public sealed class AppDirectory : IDisposable
{
    public sealed record App(
        string  Id,
        string  Name,
        string  Description,
        string? IconPath,
        string  WorkflowPath,
        // How much room the App wants beside the avatar: none, normal or wide.
        string  Pane);

    // WHAT A PERSON NEEDS TO CHOOSE AN APP AND CONSENT TO IT: its descriptor.
    public sealed record Descriptor(
        string   Id,
        string   Name,
        string   Description,
        string   Standard,
        string   Version,
        IReadOnlyList<string> Languages,
        IReadOnlyList<string> Asks,
        string   Keeps);

    private readonly HttpClient http;
    private readonly string     root;
    // Where this client's Apps are: the Service's default offer, or one collection.
    private readonly string     apps;

    // collection: the collection this client uses (MPAI_MAS_COLLECTION); none means
    // the Service's default. Only the App routes depend on it.
    public AppDirectory(string serviceUrl, string? bearerToken = null, string? clientId = null, string? collection = null)
    {
        root = serviceUrl.TrimEnd('/');
        apps = $"{root}/MPAI/AIFU/" + (string.IsNullOrWhiteSpace(collection) ? "" : $"c/{Uri.EscapeDataString(collection.Trim())}/");
        var handler = new HttpClientHandler
        {
            // A DEVELOPMENT CONVENIENCE, AND NOTHING ELSE. A Service reached over
            // a network presents a certificate the client machine trusts; this
            // accepts the development certificate so that one machine can be
            // tried without one. It has no place in anything shipped.
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        if (!string.IsNullOrWhiteSpace(bearerToken))
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        if (!string.IsNullOrWhiteSpace(clientId))
            http.DefaultRequestHeaders.Add("MPAI-Client", clientId);
    }

    // THIS CLIENT IS CLOSING: the Service stops counting it at once.
    public async Task LeaveAsync()
    {
        try { await http.PostAsync($"{root}/MPAI/AIFU/Leave", null); }
        catch { }
    }

    // HOW MANY CLIENTS ARE USING THE SERVICE NOW, this one included; null when the
    // Service does not say.
    public async Task<int?> ActiveClientsAsync()
    {
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"{root}/MPAI/AIFU/Status"));
            return doc.RootElement.GetProperty("activeClients").GetInt32();
        }
        catch { return null; }
    }

    // The Apps this Service offers. An empty list is an answer, not a failure:
    // a Service may serve Modules to clients that already know what they want.
    public async Task<IReadOnlyList<App>> ListAsync() =>
        Parse(await http.GetStringAsync($"{apps}Apps"));

    // SEARCH, by words and/or a category, among the Apps this client may see.
    public async Task<IReadOnlyList<App>> SearchAsync(string? words, string? category) =>
        Parse(await http.GetStringAsync(
            $"{apps}Apps?q={Uri.EscapeDataString(words ?? "")}&category={Uri.EscapeDataString(category ?? "")}"));

    // The categories in use among the Apps this client may see.
    public async Task<IReadOnlyList<string>> CategoriesAsync()
    {
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"{apps}Categories"));
            return doc.RootElement.EnumerateArray().Select(c => c.GetProperty("name").GetString() ?? "").Where(c => c.Length > 0).ToList();
        }
        catch { return Array.Empty<string>(); }   // a Service without categories offers none
    }

    // An App's descriptor; null when the Service has none to give.
    public async Task<Descriptor?> DescriptorAsync(string appId)
    {
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"{apps}Apps/{Uri.EscapeDataString(appId)}/Descriptor"));
            var r = doc.RootElement;
            string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            IReadOnlyList<string> L(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                : Array.Empty<string>();
            return new Descriptor(appId, S("Name"), S("Description"), S("Standard"), S("Version"), L("Languages"), L("Asks"), S("Keeps"));
        }
        catch { return null; }
    }

    private IReadOnlyList<App> Parse(string json)
    {
        var apps = new List<App>();

        using var doc = JsonDocument.Parse(json);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            string S(string name) =>
                e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() ?? "" : "";

            var icon = S("icon");
            apps.Add(new App(
                S("id"), S("name"), S("description"),
                string.IsNullOrEmpty(icon) ? null : $"{root}/MPAI/AIFU/{icon}",
                $"{root}/MPAI/AIFU/{S("workflow")}",
                S("pane") is { Length: > 0 } w ? w : "normal"));
        }
        return apps;
    }

    // The Workflow Description, as text. The client reads it with the same
    // reader it would use on a file: an App obtained from a Service and an App
    // opened from disk are the same thing, and nothing downstream can tell
    // which it was given.
    public Task<string> WorkflowAsync(App app) => http.GetStringAsync(app.WorkflowPath);

    // BY IDENTIFIER, WITHOUT LISTING. A Service holds Apps it does not offer - the
    // one a client runs in order to offer the others - and a client that looked
    // for it in the catalogue would not find it.
    public Task<string> WorkflowAsync(string appId) =>
        http.GetStringAsync($"{apps}Apps/{appId}");

    public async Task<byte[]?> IconAsync(App app)
    {
        if (app.IconPath is null) return null;
        try { return await http.GetByteArrayAsync(app.IconPath); }
        catch { return null; }   // an App without a picture is still an App
    }

    public void Dispose() => http.Dispose();
}