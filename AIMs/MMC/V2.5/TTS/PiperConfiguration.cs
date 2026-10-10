namespace Mmc.Tts.Piper;

public sealed class PiperConfiguration
{
    public string ExecutablePath { get; init; } = string.Empty;

    public TimeSpan SynthesisTimeout { get; init; }
        = TimeSpan.FromSeconds(30);

    // Keep piper running with its voice loaded (ResidentPiper), instead of starting
    // it for every text.
    public bool Resident { get; init; } = true;
}