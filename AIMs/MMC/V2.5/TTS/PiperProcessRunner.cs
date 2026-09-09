using System.Diagnostics;

namespace Mmc.Tts.Piper;

public sealed class PiperProcessRunner : IPiperProcessRunner
{
    private readonly PiperConfiguration _configuration;

    public PiperProcessRunner(
        PiperConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<byte[]> SynthesizeAsync(
        PiperSynthesisRequest request,
        CancellationToken cancellationToken = default)
    {
        var tempDirectory =
            Path.Combine(
                Path.GetTempPath(),
                "MMC-TTS",
                Guid.NewGuid().ToString());

        Directory.CreateDirectory(tempDirectory);

        try
        {
            var inputPath =
                Path.Combine(tempDirectory, "input.txt");

            var outputPath =
                Path.Combine(tempDirectory, "output.wav");

            await File.WriteAllTextAsync(
                inputPath,
                request.Text,
                cancellationToken);

            var startInfo =
                new ProcessStartInfo
                {
                    FileName =
                        _configuration.ExecutablePath,

                    Arguments =
                        $"-m \"{request.ModelPath}\" " +
                        $"-c \"{request.ConfigPath}\" " +
                        $"-f \"{outputPath}\"",

                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,

                    UseShellExecute = false,
                    CreateNoWindow = true
                };

            using var process =
                new Process
                {
                    StartInfo = startInfo
                };

            process.Start();

            await process.StandardInput.WriteAsync(
                request.Text);

            await process.StandardInput.FlushAsync();

            process.StandardInput.Close();

            var timeoutTask =
                Task.Delay(
                    _configuration.SynthesisTimeout,
                    cancellationToken);

            var processTask =
                process.WaitForExitAsync(
                    cancellationToken);

            var completedTask =
                await Task.WhenAny(
                    processTask,
                    timeoutTask);

            if (completedTask == timeoutTask)
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                }

                throw new TimeoutException(
                    "Piper synthesis timed out.");
            }

            if (!File.Exists(outputPath))
            {
                var stderr =
                    await process.StandardError
                        .ReadToEndAsync();

                throw new InvalidOperationException(
                    $"Piper did not generate output WAV. " +
                    $"Error: {stderr}");
            }

            return await File.ReadAllBytesAsync(
                outputPath,
                cancellationToken);
        }
        finally
        {
            try
            {
                Directory.Delete(
                    tempDirectory,
                    true);
            }
            catch
            {
            }
        }
    }
}