using System.Collections.Generic;
using AIF.Controller;

namespace Mpai.Osd.Ava;

// Plug-in for OSD-AVA-V1.5. No model dependencies: Create builds the processor with its
// ports. Discovered dynamically by the middleware provider (IAimPlugin).
public sealed class OsdAvaPlugin : IAimPlugin
{
    public string AimName => "OSD-AVA-V1.5";

    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new OsdAvaAimProcessor(AimName, ports);
}
