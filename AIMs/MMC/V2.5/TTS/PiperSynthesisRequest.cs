namespace Mmc.Tts.Piper;

public sealed class PiperSynthesisRequest
{
    public string Text { get; init; } = string.Empty;

    public string ModelPath { get; init; } = string.Empty;

    public string ConfigPath { get; init; } = string.Empty;

    // Extra Piper CLI flags appended verbatim (e.g. prosody: --length_scale,
    // --noise_scale, --noise_w). Empty by default, so behaviour is unchanged.
    public string ExtraArgs { get; init; } = string.Empty;
}