using System.Collections.Generic;
using AIF.Controller;

namespace Mpai.Cae.Qcv;

// Plug-in for CAE-QCV-V1.0. No model dependencies: Create builds the processor with its
// ports. Discovered dynamically by the middleware provider (IAimPlugin).
public sealed class QcvPlugin : IAimPlugin
{
    public string AimName => "CAE-QCV-V1.0";

    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new QcvAimProcessor(AimName, ports);
}
