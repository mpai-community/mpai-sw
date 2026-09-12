using System.Collections.Generic;
using AIF.Controller;

namespace Mpai.Mmc.Efi;

// Plug-in for MMC-EFI-V2.5. Builds and caches its own model instance (loaded once, reused),
// with the model path taken from settings and defaulting to the current location -
// so behaviour is unchanged now and the path becomes portable via settings later.
public sealed class EfiPlugin : IAimPlugin
{
    private HSEmotionEstimator? _dep;
    public string AimName => "MMC-EFI-V2.5";
    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new EfiAimProcessor(AimName, _dep ??= new HSEmotionEstimator(Get(settings,"HseModel",@"D:\AI\Models\hsemotion_enet_b0_8_va_mtl.onnx")), ports);
    private static string Get(IReadOnlyDictionary<string,string> s, string k, string d) => s.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : d;
}
