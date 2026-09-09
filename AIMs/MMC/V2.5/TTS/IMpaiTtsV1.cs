namespace Mmc.Tts;

public interface IMpaiTtsV1
{
    Task<SpeechObject> GenerateAsync(
        string text,
        string speechQualifier);
}