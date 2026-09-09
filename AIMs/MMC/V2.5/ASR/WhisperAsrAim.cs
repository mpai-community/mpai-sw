using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Mpai.Core;

namespace Mpai.Aims.Asr;

public sealed class WhisperAsrConfiguration
{
    public required string ExecutablePath { get; init; }   // whisper-cli(.exe)
    public required string ModelPath { get; init; }        // ggml-*.bin
    public string LanguageCode { get; init; } = "en";      // model language (e.g. base.en)
}

// ---------------------------------------------------------------------------
//  MMC-ASR worked transform: Basic Speech Object -> Basic Text Object.
//    inherit   : Language from the input Speech Qualifier, if it carried one
//    determine : the recognised Language (from the model) and text Format = UTF-8
//  Mirror image of the TTS transform.
// ---------------------------------------------------------------------------
public sealed class WhisperAsrAim : IAsrAim
{
    private readonly WhisperAsrConfiguration _config;

    public WhisperAsrAim(WhisperAsrConfiguration config) => _config = config;

    public async Task<BasicTextObject> ProcessAsync(BasicSpeechObject speech)
    {
        var wav = Path.Combine(Path.GetTempPath(), $"asr_{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(wav, speech.Data);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _config.ExecutablePath,
                Arguments = BuildArguments(wav, speech),
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

            var recognisedText = ExtractTranscription(output);

            System.Console.WriteLine($"[MMC-ASR-V2.5] heard: {recognisedText}");

            return BasicTextObject.FromText(recognisedText, BuildTextQualifier(speech));
        }
        finally
        {
            try { File.Delete(wav); } catch { }
        }
    }

    private string BuildArguments(string wav, BasicSpeechObject speech)
    {
        var arguments = $"-m \"{_config.ModelPath}\" -f \"{wav}\"";

        var language =
            speech.SpeechQualifier?.Attributes?.Metadata?.Language?.LanguageCode
            ?? _config.LanguageCode;

        var englishOnly =
            System.IO.Path.GetFileNameWithoutExtension(_config.ModelPath)
                  .EndsWith(".en", StringComparison.OrdinalIgnoreCase);

        if (!englishOnly && !string.IsNullOrWhiteSpace(language))
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

    private static string ExtractTranscription(string output)
    {
        var sb = new StringBuilder();

        foreach (var line in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            if (!line.StartsWith('[')) continue;

            var cleaned = Regex.Replace(line, @"^\[[^\]]+\]\s*", "").Trim();

            if (cleaned is "" or "[silence]" or "[BLANK_AUDIO]") continue;

            sb.AppendLine(cleaned);
        }

        return sb.ToString().Trim();
    }
}
