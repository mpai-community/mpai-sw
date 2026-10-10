using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Mpai.Core;

namespace Mpai.Aims.Asr;

// THE MODEL LOADED ONCE, NOT EVERY TURN. whisper-cli loads its model on every call -
// 0.4 s of every spoken turn with ggml-small, before a sample is heard. whisper-server
// loads it once and answers on this machine's loopback: one server per program, model
// and threads, shared by every AIM Instance that names them, started at the first
// request and started again if it has gone.
internal sealed class WhisperServer
{
    private static readonly ConcurrentDictionary<string, WhisperServer> servers = new(StringComparer.OrdinalIgnoreCase);

    public static WhisperServer For(string program, string model, int? threads, string? extraArguments = null) =>
        servers.GetOrAdd($"{program}|{model}|{threads}|{extraArguments}", _ => new WhisperServer(program, model, threads, extraArguments));

    private readonly string _program, _model;
    private readonly int? _threads;
    private readonly string? _extra;
    private readonly SemaphoreSlim _starting = new(1, 1);
    private Process? _process;
    private HttpClient? _http;

    private WhisperServer(string program, string model, int? threads, string? extraArguments) =>
        (_program, _model, _threads, _extra) = (program, model, threads, extraArguments);

    // What was said, as whisper-server writes it: plain text, captions included.
    public async Task<string> TranscribeAsync(byte[] wav, string? language, int audioContext)
    {
        for (int attempt = 0; ; attempt++)
        {
            var http = await RunningAsync().ConfigureAwait(false);
            try
            {
                using var form = new MultipartFormDataContent
                {
                    { new ByteArrayContent(wav), "file", "speech.wav" },
                    { new StringContent("json"), "response_format" },
                    { new StringContent(audioContext.ToString()), "audio_ctx" }
                };
                if (!string.IsNullOrWhiteSpace(language)) form.Add(new StringContent(language), "language");

                using var response = await http.PostAsync("/inference", form).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                return json.RootElement.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "";
            }
            catch (HttpRequestException) when (attempt == 0 && _process is { HasExited: true })
            {
                // The server went between two turns: started again, the turn asked again.
            }
        }
    }

    private async Task<HttpClient> RunningAsync()
    {
        if (_process is { HasExited: false } && _http is not null) return _http;
        await _starting.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_process is { HasExited: false } && _http is not null) return _http;
            _http?.Dispose();

            int port = FreePort();
            var arguments = $"-m \"{_model}\" --host 127.0.0.1 --port {port}";
            if (_threads is > 0) arguments += $" -t {_threads}";
            if (_extra is { Length: > 0 }) arguments += $" {_extra}";

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = _program,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }) ?? throw new InvalidOperationException("Could not start whisper-server.");
            process.EnableRaisingEvents = true;
            ResidentProcesses.Adopt(process);
            // Its progress is read and dropped: a pipe nobody reads fills, and blocks it.
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromMinutes(2) };
            var deadline = DateTime.UtcNow.AddMinutes(2);
            while (true)
            {
                if (process.HasExited)
                    throw new InvalidOperationException($"whisper-server stopped at start (exit {process.ExitCode}).");
                try { using var r = await http.GetAsync("/").ConfigureAwait(false); if (r.IsSuccessStatusCode) break; }
                catch (HttpRequestException) { }
                if (DateTime.UtcNow > deadline)
                {
                    try { process.Kill(true); } catch { }
                    throw new TimeoutException("whisper-server did not answer within 2 minutes of its start.");
                }
                await Task.Delay(100).ConfigureAwait(false);
            }

            Console.WriteLine($"[MMC-ASR-V2.5] whisper-server ready on port {port}: {System.IO.Path.GetFileName(_model)} kept loaded");
            (_process, _http) = (process, http);
            return http;
        }
        finally { _starting.Release(); }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
