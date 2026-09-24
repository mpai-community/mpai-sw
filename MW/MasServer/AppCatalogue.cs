using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Mpai.Mas.Server;

// WHAT THIS SERVICE OFFERS. An App is a Workflow Description and the few things
// a person needs in order to choose it: a name, a sentence saying what it does,
// and a picture.
//
// A client holds no application. It arrives knowing only how to render an
// avatar, capture speech and interpret a Workflow Description, asks a Service
// what it has, and runs what the user picks. Which application it runs is
// decided by a document, at the moment of choosing.
//
// One folder per App, under the directory the Service is configured with:
//
//     Apps/
//       AMQ/
//         app.json        { "Name": ..., "Description": ..., "Icon": ... }
//         MMC-AMQ.orch
//         icon.png
//
// The folder name is the App's identifier. Nothing here reads the workflow: the
// Service serves it as text and the client interprets it, which is what keeps
// the Service free of any knowledge of what its Apps do.
public sealed class AppCatalogue
{
    public sealed record Entry(
        string  Id,
        string  Name,
        string  Description,
        string? IconFile,
        string  WorkflowPath,
        string  Folder,
        // HOW MUCH ROOM THE APP WANTS BESIDE THE AVATAR: none, normal or wide. The
        // App knows what it needs to show; a client that does not recognise the
        // value gives it the usual room.
        string  Pane)
    {
        // THE MODULE THE APP RUNS OVER: named by its descriptor ("Module"), or else
        // read from its workflow's first line ("workflow X over <Module>").
        public string? Module { get; init; }

        // THE WHOLE DESCRIPTOR, as its app.json states it: what a person needs to
        // find the App, understand it and consent to it. "{}" if it has none.
        public string Descriptor { get; init; } = "{}";
    }

    private readonly Dictionary<string, Entry> byId =
        new(StringComparer.OrdinalIgnoreCase);

    private string? shellId;

    // The shell's identifier, if one is held: served by name, never offered.
    public string? ShellId => shellId;

    // What a person is offered: everything held, but not the shell.
    public IReadOnlyCollection<Entry> Apps =>
        byId.Values.Where(e => !string.Equals(e.Id, shellId, StringComparison.OrdinalIgnoreCase)).ToList();

    // Everything held, the shell included. Find serves from this.
    public IReadOnlyCollection<Entry> Held => byId.Values;
    public string? Root { get; }

    private AppCatalogue(string? root) { Root = root; }

    // Read once, at startup. An App added later needs a restart, which is honest
    // for a Service that states what it has when it starts.
    // THE SERVICE IS TOLD WHICH APPS IT OFFERS. A folder under the directory is
    // where an App's files happen to be, not a declaration that this Service
    // serves it: an App appears here because an operator named it.
    public static AppCatalogue Scan(string? root, IEnumerable<string>? named = null, string? shell = null)
    {
        var catalogue = new AppCatalogue(root);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return catalogue;

        var wanted = named?.Where(s => !string.IsNullOrWhiteSpace(s))
                           .Select(s => s.Trim()).ToList();
        if (wanted is null) wanted = new List<string>();

        // THE SHELL IS HELD AND SERVED, BUT NOT OFFERED. A client fetches it by
        // name in order to offer the others; an App that offered itself would be
        // chosen and would run inside itself.
        if (!string.IsNullOrWhiteSpace(shell)) { catalogue.shellId = shell.Trim(); wanted.Add(catalogue.shellId); }
        if (wanted.Count == 0) return catalogue;   // told nothing, offers nothing

        foreach (var id in wanted)
        {
            var folder = Path.Combine(root, id);
            if (!Directory.Exists(folder))
            {
                Console.WriteLine($"  App '{id}' was named but there is no folder for it.");
                continue;
            }
            var orch = Directory.EnumerateFiles(folder, "*.orch").FirstOrDefault();
            if (orch is null)
            {
                Console.WriteLine($"  App '{id}' has no Workflow Description.");
                continue;
            }

            string name = id, description = "", icon = "", pane = "normal", descriptor = "{}";
            string? module = null;
            var manifest = Path.Combine(folder, "app.json");
            if (File.Exists(manifest))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                    var r = doc.RootElement;
                    if (r.TryGetProperty("Name", out var n))        name        = n.GetString() ?? id;
                    if (r.TryGetProperty("Description", out var d)) description = d.GetString() ?? "";
                    if (r.TryGetProperty("Icon", out var i))        icon        = i.GetString() ?? "";
                    if (r.TryGetProperty("Pane", out var w))        pane        = w.GetString() ?? "normal";
                    if (r.TryGetProperty("Module", out var m))      module      = m.GetString();
                    descriptor = r.GetRawText();
                }
                catch
                {
                    // A manifest that will not parse leaves the App listed under its
                    // folder name. Better a nameless App than a missing one.
                }
            }

            var iconPath = string.IsNullOrWhiteSpace(icon) ? null : Path.Combine(folder, icon);
            if (iconPath is not null && !File.Exists(iconPath)) iconPath = null;

            module ??= ModuleOf(orch);
            catalogue.byId[id] = new Entry(id, name, description,
                                 iconPath is null ? null : Path.GetFileName(iconPath),
                                 orch, folder, pane) { Module = module, Descriptor = descriptor };
        }
        return catalogue;
    }

    public Entry? Find(string id) => byId.TryGetValue(id, out var e) ? e : null;

    // "workflow MMC-MAT over 1MMC-MAT-V2.5-I01" names the Module.
    private static string? ModuleOf(string orch)
    {
        try
        {
            foreach (var line in File.ReadLines(orch))
            {
                var t = line.Trim();
                if (!t.StartsWith("workflow ", StringComparison.Ordinal)) continue;
                var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var over = Array.IndexOf(parts, "over");
                return over >= 0 && over + 1 < parts.Length ? parts[over + 1] : null;
            }
        }
        catch { }
        return null;
    }

    // The catalogue as a client receives it. The icon is named, not embedded: a
    // client showing a list fetches only the few it displays.
    public string ToJson() =>
        JsonSerializer.Serialize(
            Apps.Select(a => new
            {
                id          = a.Id,
                name        = a.Name,
                description = a.Description,
                icon        = a.IconFile is null ? null : $"Apps/{a.Id}/Icon",
                workflow    = $"Apps/{a.Id}",
                pane        = a.Pane
            }),
            new JsonSerializerOptions { WriteIndented = true });
}