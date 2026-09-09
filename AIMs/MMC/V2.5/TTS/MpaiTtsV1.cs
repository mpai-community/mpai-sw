using Mmc.Tts.Piper;

namespace Mmc.Tts;

public sealed class MpaiTtsV1 : IMpaiTtsV1
{
    private readonly IPiperProcessRunner _runner;
    private readonly SpeechObjectBuilder _builder;
    private readonly MpaiTtsV1Configuration _configuration;

    public MpaiTtsV1(
        IPiperProcessRunner runner,
        SpeechObjectBuilder builder,
        MpaiTtsV1Configuration configuration)
    {
        _runner = runner;
        _builder = builder;
        _configuration = configuration;
    }

    public async Task<SpeechObject> GenerateAsync(
        string text,
        string speechQualifier)
    {
        var request =
            new PiperSynthesisRequest
            {
                Text = text,
                ModelPath = _configuration.ModelPath,
                ConfigPath = _configuration.ConfigPath
            };

        var speechData =
            await _runner.SynthesizeAsync(request);

        return _builder.Build(
            speechData,
            speechQualifier);
    }
}