using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MPAIApps.StoreApp;

// The window an Implementer sees. It holds no Store logic: every check and every
// publication is the Store Service's, reached over its API, so this window and any
// other client agree on what "published" and "refused" mean.
public sealed class StoreForm : Form
{
    private static readonly string StoreUrl =
        (Environment.GetEnvironmentVariable("MPAI_STORE") is { Length: > 0 } s ? s : "https://localhost:5020/").TrimEnd('/') + "/";

    private readonly HttpClient http = new() { BaseAddress = new Uri(StoreUrl), Timeout = TimeSpan.FromSeconds(60) };

    private readonly ListBox publishedList = new() { Dock = DockStyle.Fill };
    private readonly TextBox statusBox = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Font = new System.Drawing.Font("Consolas", 9)
    };

    public StoreForm()
    {
        Text = "MPAI Store";
        Width = 900;
        Height = 560;
        StartPosition = FormStartPosition.CenterScreen;

        var submitButton = new Button { Text = "Submit an L3...", Dock = DockStyle.Top, Height = 36 };
        submitButton.Click += async (_, _) => await SubmitFileAsync();

        var refreshButton = new Button { Text = "Refresh", Dock = DockStyle.Top, Height = 28 };
        refreshButton.Click += async (_, _) => await RefreshListAsync();

        var leftPanel = new Panel { Dock = DockStyle.Left, Width = 320 };
        leftPanel.Controls.Add(publishedList);
        leftPanel.Controls.Add(new Label { Text = $"Published in the Store at {StoreUrl}", Dock = DockStyle.Top, Height = 20 });
        leftPanel.Controls.Add(refreshButton);
        leftPanel.Controls.Add(submitButton);

        Controls.Add(statusBox);
        Controls.Add(leftPanel);

        Shown += async (_, _) => await RefreshListAsync();
    }

    private async Task RefreshListAsync()
    {
        publishedList.Items.Clear();
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync("MPAI/Store/L3"));
            foreach (var e in doc.RootElement.EnumerateArray())
                publishedList.Items.Add($"{e.GetProperty("id").GetString()}  v{e.GetProperty("version").GetInt32()}" +
                                        $"   ({e.GetProperty("errors").GetInt32()} err, {e.GetProperty("warnings").GetInt32()} warn)");
        }
        catch (Exception failure)
        {
            Log($"Could not reach the Store at {StoreUrl}: {failure.Message}");
        }
    }

    // The one entry point an Implementer uses: pick an L3, submit it, read the verdict.
    private async Task SubmitFileAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Submit an L3 (AIM Metadata instance)",
            Filter = "AIM Metadata (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        string json;
        try { json = File.ReadAllText(dialog.FileName); }
        catch (Exception failure) { Log($"Could not read {dialog.FileName}: {failure.Message}"); return; }

        Log($"--- {Path.GetFileName(dialog.FileName)} ---");
        try
        {
            using var answer = await http.PostAsync("MPAI/Store/L3", new StringContent(json, Encoding.UTF8, "application/json"));
            using var doc = JsonDocument.Parse(await answer.Content.ReadAsStringAsync());
            var r = doc.RootElement;

            if (r.TryGetProperty("findings", out var findings))
                foreach (var f in findings.EnumerateArray())
                    Log($"    {f.GetProperty("severity").GetString(),-7} [{f.GetProperty("source").GetString()}] {f.GetProperty("text").GetString()}");

            if (r.TryGetProperty("published", out var p) && p.GetBoolean())
            {
                Log($"    PUBLISHED {r.GetProperty("id").GetString()} as version {r.GetProperty("version").GetInt32()}");
                await RefreshListAsync();
            }
            else
            {
                var why = r.TryGetProperty("refused", out var reason) ? reason.GetString() : $"The Store answered {(int)answer.StatusCode}.";
                Log($"    REFUSED   {why}");
                MessageBox.Show(this, why, "Refused", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (Exception failure)
        {
            Log($"    Could not submit to the Store at {StoreUrl}: {failure.Message}");
        }
    }

    private void Log(string line) => statusBox.AppendText(line + Environment.NewLine);
}
