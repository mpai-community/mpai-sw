using System.Collections.Generic;
using AIF.Controller;

namespace Mpai.Paf.Fpe;

// Plug-in for PAF-FPE-V1.6. Builds and caches its own model instance (loaded once, reused),
// with the model path taken from settings and defaulting to the current location -
// so behaviour is unchanged now and the path becomes portable via settings later.
public sealed class FpePlugin : IAimPlugin
{
    private HSEmotionEstimator? _dep;
    public string AimName => "PAF-FPE-V1.6";
    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new FpeAimProcessor(AimName, _dep ??= new HSEmotionEstimator(Get(settings,"HseModel",Mpai.Core.MpaiPaths.Model(@"hsemotion_enet_b0_8_va_mtl.onnx"))), ports);
    private static string Get(IReadOnlyDictionary<string,string> s, string k, string d) => s.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : d;
}
