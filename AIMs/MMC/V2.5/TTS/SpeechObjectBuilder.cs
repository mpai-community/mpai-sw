namespace Mmc.Tts;

public sealed class SpeechObjectBuilder
{
    public SpeechObject Build(
        byte[] speechData,
        string speechQualifier)
    {
        return new SpeechObject
        {
            SpeechData = speechData,
            SpeechQualifier = speechQualifier
        };
    }
}