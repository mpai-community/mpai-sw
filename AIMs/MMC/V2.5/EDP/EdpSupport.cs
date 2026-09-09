using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
public sealed class OllamaClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _model;

    public OllamaClient(string model = "llama3.1", string baseUrl = "http://127.0.0.1:11434")
    {
        _model = model;
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(5) };
    }

    // Send a system + user prompt, return the assistant's text reply.
    public async Task<string> ChatAsync(string system, string user)
    {
        var request = new
        {
            model = _model,
            stream = false,
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user",   content = user }
            }
        };

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
