using System.Collections.Generic;
using AIF.Controller;

namespace Mpai.Mmc.Edp;

// Plug-in for MMC-EDP-V2.5. Builds and caches its own model instance (loaded once, reused),
// with the model path taken from settings and defaulting to the current location -
// so behaviour is unchanged now and the path becomes portable via settings later.
public sealed class EdpPlugin : IAimPlugin
{
    private OllamaClient? _dep;
    public string AimName => "MMC-EDP-V2.5";
    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new EdpAimProcessor(AimName, _dep ??= new OllamaClient(Get(settings,"OllamaModel","llama3.1")), ports);
    private static string Get(IReadOnlyDictionary<string,string> s, string k, string d) => s.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : d;
}
