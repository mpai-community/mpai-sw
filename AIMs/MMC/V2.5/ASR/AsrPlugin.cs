using System.Collections.Generic;
using AIF.Controller;

namespace Mpai.Aims.Asr;

// Plug-in for MMC-ASR-V2.5. The engine is built by AsrFactory from settings (paths, options),
// exactly as before - so this plug-in is a self-contained construction of the AIM.
public sealed class AsrPlugin : IAimPlugin
{
    public string AimName => "MMC-ASR-V2.5";
    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new AsrAimProcessor(AimName, AsrFactory.Create(settings), ports);
}
