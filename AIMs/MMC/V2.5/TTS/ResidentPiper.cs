using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

using Mpai.Core;

namespace Mmc.Tts.Piper;

// THE VOICE LOADED ONCE, NOT EVERY TURN. Started for one text, piper loads its voice
// every time - 0.3 s of every answer before a sample is made. Kept running with an
// output directory, it reads one text per line and writes one WAV per line, naming it
// on its standard output: the voice is loaded once.
//
// One process per program, voice and extra arguments (prosody is given on the command
// line, so a prosody is a process of its own); the least recently used is retired
// when more are wanted than Limit.
internal sealed class ResidentPiper
{
    private const int Limit = 8;
    private static readonly ConcurrentDictionary<string, ResidentPiper> pipers = new(StringComparer.Ordinal);

    public static ResidentPiper For(string program, PiperSynthesisRequest request)
    {
        var key = $"{program}|{request.ModelPath}|{request.ConfigPath}|{request.ExtraArgs}";
        var piper = pipers.GetOrAdd(key, _ => new ResidentPiper(program, request));
        piper._used = DateTime.UtcNow;
        if (pipers.Count > Limit)
            foreach (var old in pipers.OrderBy(p => p.Value._used).Take(pipers.Count - Limit).ToList())
                if (pipers.TryRemove(old.Key, out var retired)) retired.Retire();
        return piper;
    }

    private readonly string _program, _arguments, _directory;
    private readonly SemaphoreSlim _one = new(1, 1);
    private Process? _process;
    private DateTime _used = DateTime.UtcNow;

    private ResidentPiper(string program, PiperSynthesisRequest request)
    {
        _program = program;
        _directory = Path.Combine(Path.GetTempPath(), "MMC-TTS", "resident-" + Guid.NewGuid().ToString("N"));
        _arguments = $"-m \"{request.ModelPath}\" -c \"{request.ConfigPath}\" --output_dir \"{_directory}\"" +
                     (string.IsNullOrWhiteSpace(request.ExtraArgs) ? "" : " " + request.ExtraArgs);
    }

    // One text, one WAV. A line is one text to piper, so the text is made one line.
    public async Task<byte[]> SynthesizeAsync(string text, TimeSpan timeout, CancellationToken cancellation)
    {
        var line = string.Join(' ', text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        await _one.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                var process = Running();
                try
                {
                    await process.StandardInput.WriteLineAsync(line).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(cancellation).ConfigureAwait(false);

                    var written = process.StandardOutput.ReadLineAsync(cancellation).AsTask();
                    if (await Task.WhenAny(written, Task.Delay(timeout, cancellation)).ConfigureAwait(false) != written)
                        throw new TimeoutException("Piper synthesis timed out.");
                    var path = (await written.ConfigureAwait(false))?.Trim();
                    if (string.IsNullOrEmpty(path))
                        throw new IOException("piper stopped.");

                    var wav = await File.ReadAllBytesAsync(path, cancellation).ConfigureAwait(false);
                    try { File.Delete(path); } catch { }
                    return wav;
                }
                catch (Exception e) when ((e is IOException or TimeoutException or InvalidOperationException) && !cancellation.IsCancellationRequested)
                {
                    // Gone or stuck: put down, and - once - started again for this text.
                    Retire();
                    if (attempt > 0 || e is TimeoutException) throw;
                }
            }
        }
        finally { _one.Release(); }
    }

    private Process Running()
    {
        if (_process is { HasExited: false }) return _process;
        Directory.CreateDirectory(_directory);
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = _program,
            Arguments = _arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8
        }) ?? throw new InvalidOperationException("Could not start piper.");
        process.EnableRaisingEvents = true;
        ResidentProcesses.Adopt(process);
        // Its log is read and dropped: a pipe nobody reads fills, and blocks it.
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        return _process = process;
    }

    private void Retire()
    {
        try { if (_process is { HasExited: false }) _process.Kill(true); } catch { }
        _process = null;
    }
}
