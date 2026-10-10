using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Aims.Asr;

public sealed class WhisperAsrConfiguration
{
    public required string ExecutablePath { get; init; }   // whisper-cli(.exe)
    public required string ModelPath { get; init; }        // ggml-*.bin
    public string LanguageCode { get; init; } = "en";      // model language (e.g. base.en)

    // whisper-server(.exe): where named, the model is loaded once and kept, and every
    // turn is answered by it; where not, whisper-cli loads it at every turn.
    public string? ServerPath { get; init; }

    // SPEED, TRADED KNOWINGLY. Null leaves whisper's own default.
    //   Threads      - CPU threads; whisper uses 4 unless told, whatever the machine has.
    //   AudioContext - encoder window in 20 ms steps (1500 = the full 30 s). Whisper
    //                  encodes the whole window however short the utterance, so a
    //                  smaller one is much faster - and anything spoken beyond it is lost.
    //                  Null: sized to each recording (see Window), so nothing is lost.
    public int? Threads { get; init; }
    public int? AudioContext { get; init; }

    // Switches added to whisper-server's and whisper-cli's command lines, as the settings give them
    // (ExtraArguments). For a GPU build on a card where one of whisper's own defaults fails: on
    // an RTX PRO 4500 Blackwell, CUDA 12.8, flash attention (on by default) made the same recording
    // come out whole, cut short, or empty from one request to the next; "-nfa" (no flash attention)
    // gave the whole sentence every time. Null: nothing added.
    public string? ExtraArguments { get; init; }
}

// ---------------------------------------------------------------------------
//  MMC-ASR worked transform: Basic Speech Object -> Basic Text Object.
//    inherit   : Language from the input Speech Qualifier, if it carried one
//    determine : the recognised Language (from the model) and text Format = UTF-8
//  Mirror image of the TTS transform.
//
//  The Speech Object DECLARES its audio format in its Speech Qualifier (PCM
//  sampling frequency + precision). whisper-cli needs a real WAV file, so the
//  bytes are written as a valid WAV: passed through when already a RIFF/WAVE
//  container (the stand-alone apps supply that), otherwise the raw PCM is wrapped
//  in a WAV header using the format the qualifier declares.
// ---------------------------------------------------------------------------
public sealed class WhisperAsrAim : IAsrAim
{
    private readonly WhisperAsrConfiguration _config;

    public WhisperAsrAim(WhisperAsrConfiguration config) => _config = config;

    public async Task<BasicTextObject> ProcessAsync(BasicSpeechObject speech)
    {
        var bytes = ToWavBytes(speech);
        int window = _config.AudioContext ?? Window(bytes);

        if (_config.ServerPath is { Length: > 0 } server)
        {
            var language = EnglishOnly ? null : PrimaryLanguage(Language(speech));
            var said = await WhisperServer.For(server, _config.ModelPath, _config.Threads, _config.ExtraArguments)
                                          .TranscribeAsync(bytes, language, window);
            return Heard(Clean(said.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None), out var noise), noise, speech);
        }

        var wav = Path.Combine(Path.GetTempPath(), $"asr_{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(wav, bytes);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _config.ExecutablePath,
                Arguments = BuildArguments(wav, speech, window),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,

                // whisper-cli writes UTF-8. Unless told so, .NET decodes a child
                // process's output using the console's code page - Windows-1252
                // here - and any transcription outside Latin-1 arrives as mojibake.
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding  = System.Text.Encoding.UTF8
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Could not start whisper-cli.");

            // Read stdout AND stderr concurrently, then wait for exit. whisper-cli
            // writes copious progress to stderr; if stderr is not drained, its write
            // blocks once the pipe buffer fills (a windowed app has no console to
            // absorb it), and the process never exits - a deadlock. Draining both
            // streams before WaitForExit avoids it.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync();
            var output = stdoutTask.Result;

            // whisper-cli writes each segment as "[timestamps] text".
            var segments = Array.FindAll(output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None), l => l.StartsWith('['));
            return Heard(Clean(segments, out var sounds), sounds, speech);
        }
        finally
        {
            try { File.Delete(wav); } catch { }
        }
    }

    private BasicTextObject Heard(string recognisedText, string sounds, BasicSpeechObject speech)
    {
        // What a person said is theirs: the log says that something was heard, and
        // what only when diagnostics are on (MpaiDiag).
        System.Console.WriteLine(recognisedText.Length == 0 && sounds.Length > 0
            ? $"[MMC-ASR-V2.5] heard only a sound{(MpaiDiag.Enabled ? $": {sounds}" : "")} - ignored"
            : MpaiDiag.Enabled ? $"[MMC-ASR-V2.5] heard: {recognisedText}"
                               : $"[MMC-ASR-V2.5] heard {recognisedText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length} words");

        return BasicTextObject.FromText(recognisedText, BuildTextQualifier(speech));
    }

    // THE WINDOW FITS THE RECORDING. Whisper encodes its whole window whatever is in
    // it, so a window much longer than the speech wastes time, and one shorter loses
    // the end of it. The recording, with 5 s to spare (whisper misreads the last
    // words of a window it fills), never less than 512 (~10 s), never more than the
    // model's 1500 (30 s).
    private static int Window(byte[] wav)
    {
        double seconds = 30;
        if (wav.Length > 44 && wav[0] == (byte)'R')
        {
            int byteRate = BitConverter.ToInt32(wav, 28);
            if (byteRate > 0) seconds = (wav.Length - 44) / (double)byteRate;
        }
        return Math.Clamp((int)Math.Ceiling((seconds + 5) * 50), 512, 1500);
    }

    private bool EnglishOnly =>
        Path.GetFileNameWithoutExtension(_config.ModelPath).EndsWith(".en", StringComparison.OrdinalIgnoreCase);

    private string? Language(BasicSpeechObject speech) =>
        speech.SpeechQualifier?.Attributes?.Metadata?.Language?.LanguageCode ?? _config.LanguageCode;

    // Produce a valid WAV byte[] for whisper-cli from the Speech Object.
    // Pass through a RIFF/WAVE container; otherwise wrap the raw PCM in a WAV
    // header using the format declared by the Speech Qualifier.
    private static byte[] ToWavBytes(BasicSpeechObject speech)
    {
        var data = speech.Data ?? Array.Empty<byte>();
        if (data.Length == 0) return data;

        // Already a WAV container? Pass through.
        if (data.Length >= 4 && data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F')
            return data;

        var pcm = speech.SpeechQualifier?.Format?.ContentFormats?.RawData;
        int rate = pcm?.SamplingFrequency is double f && f > 0 ? (int)f : 16000;
        int bits = pcm?.Precision ?? 16;
        int channels = speech.SpeechQualifier?.Attributes?.Device?.CaptureConfiguration?.ChannelCount ?? 1;
        if (channels <= 0) channels = 1;

        return WrapPcmInWav(data, rate, channels, bits);
    }

    // Build a canonical 44-byte-header WAV around interleaved PCM.
    private static byte[] WrapPcmInWav(byte[] pcm, int sampleRate, int channels, int bits)
    {
        int byteRate   = sampleRate * channels * bits / 8;
        int blockAlign = channels * bits / 8;
        int dataLen    = pcm.Length;

        using var ms = new MemoryStream(44 + dataLen);
        using var bw = new BinaryWriter(ms);
        bw.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
        bw.Write(36 + dataLen);
        bw.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
        bw.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
        bw.Write(16);                       // fmt chunk size
        bw.Write((short)1);                 // PCM
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write((short)blockAlign);
        bw.Write((short)bits);
        bw.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
        bw.Write(dataLen);
        bw.Write(pcm);
        bw.Flush();
        return ms.ToArray();
    }

    private string BuildArguments(string wav, BasicSpeechObject speech, int window)
    {
        var arguments = $"-m \"{_config.ModelPath}\" -f \"{wav}\" -ac {window}";

        if (_config.Threads is > 0 and var threads)
            arguments += $" -t {threads}";

        if (_config.ExtraArguments is { Length: > 0 } extra)
            arguments += $" {extra}";

        var language = Language(speech);

        if (!EnglishOnly && !string.IsNullOrWhiteSpace(language))
        {
            arguments += $" -l {PrimaryLanguage(language)}";
        }

        return arguments;
    }

    private static string PrimaryLanguage(string languageCode)
    {
        var head = languageCode.Trim().ToLowerInvariant().Split('-', '_')[0];

        return head is "auto" || head.Length <= 2 ? head : head.Substring(0, 2);
    }

    private TextQualifier BuildTextQualifier(BasicSpeechObject source)
    {
        Language? language =
            source.SpeechQualifier?.Attributes?.Metadata?.Language
            ?? new Language { LanguageCode = _config.LanguageCode, LanguageFormat = LanguageFormat.Iso639_1 };

        return new TextQualifier
        {
            TextQualifierID = Guid.NewGuid().ToString(),
            Format = new TextFormat
            {
                ContentFormat = new TextContentFormat { Static = TextStaticFormat.Utf8 }
            },
            Attributes = new TextAttributes { Language = language }
        };
    }

    // WHAT WAS SAID, NOT WHAT WAS HEARD. Whisper learned from subtitles, and
    // writes a sound as a caption - "(spoon clanks)", "[BLANK_AUDIO]", "[Music]".
    // A caption is not speech: it is taken out, and a turn that was only a sound
    // has no words. sounds: the captions taken out, for the log.
    private static readonly Regex Caption = new(@"\([^()]*\)|\[[^\[\]]*\]", RegexOptions.Compiled);

    private static string Clean(string[] lines, out string sounds)
    {
        var sb = new StringBuilder();
        var heard = new System.Collections.Generic.List<string>();

        foreach (var line in lines)
        {
            var cleaned = Regex.Replace(line, @"^\[\d\d:[^\]]*-->[^\]]*\]\s*", "").Trim();   // the timestamps
            foreach (Match caption in Caption.Matches(cleaned)) heard.Add(caption.Value);
            cleaned = Regex.Replace(Caption.Replace(cleaned, " "), @"\s+", " ").Trim();

            if (cleaned.Length == 0) continue;

            sb.AppendLine(cleaned);
        }

        sounds = string.Join(" ", heard);
        return sb.ToString().Trim();
    }
}
