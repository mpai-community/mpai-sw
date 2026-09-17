using System.Collections.Generic;
using AIF.Controller;

namespace Mpai.Osd.Bls;

// Plug-in for OSD-BLS-V1.5. No model dependencies: Create builds the processor with its
// ports. Discovered dynamically by the middleware provider (IAimPlugin).
public sealed class BlsPlugin : IAimPlugin
{
    public string AimName => "OSD-BLS-V1.5";

    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new BlsAimProcessor(AimName, ports);
}
