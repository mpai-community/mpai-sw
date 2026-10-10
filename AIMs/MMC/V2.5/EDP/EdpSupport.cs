using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Mpai.Mmc.Edp;

// MMC-SUM-V2.5 - Summary. The running dialogue memory: a plain-text running summary
// of the conversation (carried in SummaryData), which EDP reads in and writes back
// updated. Mirrors schemas/MMC/V2.5/data/Summary.json (the full schema also carries
// structured SummaryElements; this implementation uses the plain-text SummaryData
// for the LLM's working memory).
public sealed class Summary
{
    public string Header { get; init; } = "MMC-SUM-V2.5";
    public string SummaryID { get; init; } = Guid.NewGuid().ToString();
    public List<SummaryDataItem> SummaryData { get; init; } = new();

    public string Text()
        => SummaryData.Count > 0 && SummaryData[0].Data is not null ? SummaryData[0].Data! : "";

    public static Summary Of(string text) => new()
    {
        SummaryData = new List<SummaryDataItem> { new() { Data = text } }
    };
}

public sealed class SummaryDataItem
{
    public string? Data { get; init; }
    public string? DataURI { get; init; }
    public long? DataLength { get; init; }
    public string? DataID { get; init; }
}

// Minimal client for a local Ollama server (http://127.0.0.1:11434). Uses the
// /api/chat endpoint with stream disabled, and returns the assistant's message
// content. HttpClient + System.Text.Json only - no external package.
//
// THE SAME MODEL, THE SAME WAY, ON EVERY MACHINE. Ollama chooses a context from the
// memory it finds - 4096 tokens on a 4 GB card, 32768 on a 24 GB one, whose cache is
// larger to set up and to fill at every request - and unloads a model 5 minutes after
// its last use, so the first turn after a pause loads it again. The context is
// therefore stated (always the same: a different one reloads the model), and the
// model kept loaded.
public sealed class OllamaClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly int _context;
    private readonly int _keepAlive;

    // context: tokens of prompt and answer. keepAliveSeconds: how long the model stays
    // loaded after a request; negative, for as long as Ollama runs.
    public OllamaClient(string model = "llama3.1", string baseUrl = "http://127.0.0.1:11434",
                        int context = 4096, int keepAliveSeconds = -1)
    {
        _model = model;
        _context = context;
        _keepAlive = keepAliveSeconds;
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(5) };
    }

    // Send a system + user prompt, return the assistant's text reply.
    public Task<string> ChatAsync(string system, string user) =>
        PostAsync(new
        {
            model = _model,
            stream = false,
            keep_alive = _keepAlive,
            options = new { num_ctx = _context },
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user",   content = user }
            }
        });

    // The same, the reply a JSON object, and the same reply to the same prompt
    // (temperature 0): for understanding, where what is understood is then checked.
    public Task<string> ChatJsonAsync(string system, string user) =>
        PostAsync(new
        {
            model = _model,
            stream = false,
            format = "json",
            keep_alive = _keepAlive,
            options = new { temperature = 0, seed = 1, num_ctx = _context },
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user",   content = user }
            }
        });

    // The same, the reply forced to a JSON Schema (Ollama's structured output): the
    // model can only answer with what the schema allows.
    public Task<string> ChatSchemaAsync(string system, IEnumerable<(string Role, string Content)> turns, JsonNode schema) =>
        PostAsync(new
        {
            model = _model,
            stream = false,
            format = schema,
            keep_alive = _keepAlive,
            options = new { temperature = 0, seed = 1, num_ctx = _context },
            messages = new[] { new { role = "system", content = system } }
                .Concat(turns.Select(t => new { role = t.Role, content = t.Content })).ToArray()
        });

    private async Task<string> PostAsync(object request)
    {
        var json = JsonSerializer.Serialize(request);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("/api/chat", content).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        // Ollama /api/chat (non-stream): { "message": { "role":"assistant", "content":"..." }, ... }
        if (doc.RootElement.TryGetProperty("message", out var msg) &&
            msg.TryGetProperty("content", out var contentEl))
            return contentEl.GetString() ?? "";
        return "";
    }

    public void Dispose() => _http.Dispose();
}
