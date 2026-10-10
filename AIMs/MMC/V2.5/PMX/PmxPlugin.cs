using System.Collections.Generic;
using AIF.Controller;

namespace Mpai.Mmc.Pmx;

// Plug-in for MMC-PMX-V2.5. No model dependencies: Create builds the processor with its
// ports. Discovered dynamically by the middleware provider (IAimPlugin).
public sealed class PmxPlugin : IAimPlugin
{
    public string AimName => "MMC-PMX-V2.5";

    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new PmxAimProcessor(AimName, ports);
}
