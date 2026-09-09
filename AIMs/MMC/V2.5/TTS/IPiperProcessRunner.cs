namespace Mmc.Tts.Piper;

public interface IPiperProcessRunner
{
    Task<byte[]> SynthesizeAsync(
        PiperSynthesisRequest request,
        CancellationToken cancellationToken = default);
}