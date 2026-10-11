using System.Collections.Generic;
using AIF.Controller;

namespace Mpai.Cae.Asi;

// Plug-in for CAE-ASI-V2.5. Builds and caches its own model instance (loaded once, reused),
// with the model path taken from settings and defaulting to the current location -
// so behaviour is unchanged now and the path becomes portable via settings later.
public sealed class AsiPlugin : IAimPlugin
{
    private SoundClassifier? _dep;
    public string AimName => "CAE-ASI-V2.5";
    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new CaeAsiAimProcessor(AimName, _dep ??= new SoundClassifier(Get(settings,"YamnetModel",@"D:\AI\Models\yamnet.onnx"), Get(settings,"YamnetClassMap",@"D:\AI\Models\yamnet_class_map.csv")), ports);
    private static string Get(IReadOnlyDictionary<string,string> s, string k, string d) => s.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : d;
}
