using System.Collections.Generic;
using AIF.Controller;

namespace Mpai.Paf.Psd;

// Plug-in for PAF-PSD-V1.6. No model dependencies: Create builds the processor with its
// ports. Discovered dynamically by the middleware provider (IAimPlugin).
public sealed class PsdPlugin : IAimPlugin
{
    public string AimName => "PAF-PSD-V1.6";

    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new PsdAimProcessor(AimName, ports);
}
