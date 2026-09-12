using System.Collections.Generic;
using AIF.Controller;

namespace Mpai.Aims.Ttt;

// Plug-in for MMC-TTT-V2.5. The engine is built by TttFactory from settings (paths, options),
// exactly as before - so this plug-in is a self-contained construction of the AIM.
public sealed class TttPlugin : IAimPlugin
{
    public string AimName => "MMC-TTT-V2.5";
    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new TttAimProcessor(AimName, TttFactory.Create(settings), ports);
}
