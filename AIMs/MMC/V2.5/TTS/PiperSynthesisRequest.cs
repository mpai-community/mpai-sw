namespace Mmc.Tts.Piper;

public sealed class PiperSynthesisRequest
{
    public string Text { get; init; } = string.Empty;

    public string ModelPath { get; init; } = string.Empty;

    public string ConfigPath { get; init; } = string.Empty;
}