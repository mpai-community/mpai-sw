using System.Collections.Generic;
using AIF.Controller;

namespace Mpai.Paf.Sar;

// Plug-in for PAF-SAR-V1.6. No model dependencies: Create builds the processor with its
// ports. Discovered dynamically by the middleware provider (IAimPlugin).
public sealed class SarPlugin : IAimPlugin
{
    public string AimName => "PAF-SAR-V1.6";

    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new SarAimProcessor(AimName, ports, settings);
}
