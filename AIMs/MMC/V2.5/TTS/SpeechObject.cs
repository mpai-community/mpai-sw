namespace Mmc.Tts;

public sealed class SpeechObject
{
    public byte[] SpeechData { get; init; }
        = Array.Empty<byte>();

    public string SpeechQualifier { get; init; }
        = string.Empty;
}