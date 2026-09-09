namespace Mmc.Tts.Piper;

public sealed class PiperConfiguration
{
    public string ExecutablePath { get; init; } = string.Empty;

    public TimeSpan SynthesisTimeout { get; init; }
        = TimeSpan.FromSeconds(30);
}