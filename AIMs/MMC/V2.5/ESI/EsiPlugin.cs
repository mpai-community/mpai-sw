using System.Collections.Generic;
using AIF.Controller;

namespace Mpai.Mmc.Esi;

// Plug-in for MMC-ESI-V2.5. Builds and caches its own model instance (loaded once, reused),
// with the model path taken from settings and defaulting to the current location -
// so behaviour is unchanged now and the path becomes portable via settings later.
public sealed class EsiPlugin : IAimPlugin
{
    private Wav2Vec2EmotionEstimator? _dep;
    public string AimName => "MMC-ESI-V2.5";
    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new EsiAimProcessor(AimName, _dep ??= new Wav2Vec2EmotionEstimator(Get(settings,"W2v2Model",@"D:\AI\Models\w2v2-emotion\model.onnx")), ports);
    private static string Get(IReadOnlyDictionary<string,string> s, string k, string d) => s.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : d;
}
