using System.Collections.Generic;
using AIF.Controller;

namespace Mpai.Mmc.Psm;

// Plug-in for MMC-PSM-V2.5. No model dependencies: Create builds the processor with its
// ports. Discovered dynamically by the middleware provider (IAimPlugin).
public sealed class PsmPlugin : IAimPlugin
{
    public string AimName => "MMC-PSM-V2.5";

    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new PsmAimProcessor(AimName, ports);
}
