using System.Collections.Generic;
using AIF.Controller;

namespace Mpai.Osd.Bas;

// Plug-in for OSD-BAS-V1.5. No model dependencies: Create builds the processor with its
// ports. Discovered dynamically by the middleware provider (IAimPlugin).
public sealed class BasPlugin : IAimPlugin
{
    public string AimName => "OSD-BAS-V1.5";

    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new BasAimProcessor(AimName, ports);
}
