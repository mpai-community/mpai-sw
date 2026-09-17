using System.Collections.Generic;
using AIF.Controller;
using Mpai.Osd.VisualScene;

namespace Mpai.Cve.Vsi;

// Plug-in for CVE-VSI-V1.0. Builds and caches its own model instance (loaded once, reused),
// with the model path taken from settings and defaulting to the current location -
// so behaviour is unchanged now and the path becomes portable via settings later.
public sealed class VsiPlugin : IAimPlugin
{
    private ScrfdFaceDetector? _dep;
    public string AimName => "CVE-VSI-V1.0";
    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new CveVsiAimProcessor(AimName, _dep ??= new ScrfdFaceDetector(Get(settings,"ScrfdModel",@"D:\AI\Models\scrfd_10g_bnkps.onnx")), ports);
    private static string Get(IReadOnlyDictionary<string,string> s, string k, string d) => s.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : d;
}
