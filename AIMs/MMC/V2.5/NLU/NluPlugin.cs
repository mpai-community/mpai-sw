using System.Collections.Generic;
using AIF.Controller;

namespace Mpai.Mmc.Nlu;

// Plug-in for MMC-NLU-V2.5. No model dependencies: Create builds the processor with its
// ports. Discovered dynamically by the middleware provider (IAimPlugin).
public sealed class NluPlugin : IAimPlugin
{
    public string AimName => "MMC-NLU-V2.5";

    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new NluAimProcessor(AimName, ports);
}
