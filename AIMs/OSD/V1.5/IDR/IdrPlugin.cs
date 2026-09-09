using System.Collections.Generic;
using AIF.Controller;

namespace Mpai.Osd.Idr;

// Plug-in wrapper for Identity Reconciliation. IDR has no model dependencies, so
// Create simply constructs the processor with its ports. Discovered dynamically by
// the middleware provider via IAimPlugin - no compile-time reference required.
public sealed class IdrPlugin : IAimPlugin
{
    public string AimName => "OSD-IDR-V1.5";

    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new IdrAimProcessor(AimName, ports);
}
