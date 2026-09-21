using System;
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

    private readonly HttpClient http;
    private readonly string     root;

    public AppDirectory(string serviceUrl, string? bearerToken = null)
    {
        root = serviceUrl.TrimEnd('/');
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
    }

    // The Apps this Service offers. An empty list is an answer, not a failure:
    // a Service may serve Modules to clients that already know what they want.
    public async Task<IReadOnlyList<App>> ListAsync()
    {
        var json = await http.GetStringAsync($"{root}/MPAI/AIFU/Apps");
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
        http.GetStringAsync($"{root}/MPAI/AIFU/Apps/{appId}");

    public async Task<byte[]?> IconAsync(App app)
    {
        if (app.IconPath is null) return null;
        try { return await http.GetByteArrayAsync(app.IconPath); }
        catch { return null; }   // an App without a picture is still an App
    }

    public void Dispose() => http.Dispose();
}