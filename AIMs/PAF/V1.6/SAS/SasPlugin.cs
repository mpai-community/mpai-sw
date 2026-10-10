using System.Collections.Generic;
using AIF.Controller;

namespace Mpai.Paf.Sas;

// Plug-in for PAF-SAS-V1.6. No model dependencies: Create builds the processor with its
// ports. Discovered dynamically by the middleware provider (IAimPlugin).
public sealed class SasPlugin : IAimPlugin
{
    public string AimName => "PAF-SAS-V1.6";

    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new SasAimProcessor(AimName, ports);
}
