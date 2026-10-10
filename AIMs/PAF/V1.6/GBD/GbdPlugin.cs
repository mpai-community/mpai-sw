using System.Collections.Generic;
using AIF.Controller;

namespace Mpai.Paf.Gbd;

// Plug-in for PAF-GBD-V1.6. No model dependencies: Create builds the processor with its
// ports. Discovered dynamically by the middleware provider (IAimPlugin).
public sealed class GbdPlugin : IAimPlugin
{
    public string AimName => "PAF-GBD-V1.6";

    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new GbdAimProcessor(AimName, ports);
}
