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

    private readonly HttpClient http;
    public WebAppDirectory(HttpClient http) => this.http = http;

    public async Task<IReadOnlyList<App>> ListAsync()
    {
        var json = await http.GetStringAsync("MPAI/AIFU/Apps");
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

    public Task<string> WorkflowAsync(string appId) => http.GetStringAsync($"MPAI/AIFU/Apps/{appId}");
}
