using System.Collections.Generic;
using AIF.Controller;

namespace Mpai.Paf.Gfd;

// Plug-in for PAF-GFD-V1.6. No model dependencies: Create builds the processor with its
// ports. Discovered dynamically by the middleware provider (IAimPlugin).
public sealed class GfdPlugin : IAimPlugin
{
    public string AimName => "PAF-GFD-V1.6";

    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new GfdAimProcessor(AimName, ports);
}
