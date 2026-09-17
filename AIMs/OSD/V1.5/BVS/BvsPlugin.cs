using System.Collections.Generic;
using AIF.Controller;

namespace Mpai.Osd.Bvs;

// Plug-in for OSD-BVS-V1.5. No model dependencies: Create builds the processor with its
// ports. Discovered dynamically by the middleware provider (IAimPlugin).
public sealed class BvsPlugin : IAimPlugin
{
    public string AimName => "OSD-BVS-V1.5";

    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new BvsAimProcessor(AimName, ports);
}
