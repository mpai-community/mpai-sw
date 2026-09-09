using System.Collections.Generic;
using AIF.Controller;

namespace Mpai.Aims.Tts;

// Plug-in for MMC-TTS-V2.5. The engine is built by TtsFactory from settings (paths, options),
// exactly as before - so this plug-in is a self-contained construction of the AIM.
public sealed class TtsPlugin : IAimPlugin
{
    public string AimName => "MMC-TTS-V2.5";
    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new TtsAimProcessor(AimName, TtsFactory.Create(settings), ports);
}
