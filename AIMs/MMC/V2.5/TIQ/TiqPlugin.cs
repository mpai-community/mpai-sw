using System.Collections.Generic;
using AIF.Controller;

namespace Mpai.Mmc.Tiq;

// Plug-in for MMC-TIQ-V2.5. Builds the BLIP-backed processor from the model
// paths in settings (VisionModel, EncoderModel, DecoderModel, VocabFile).
// Discovered dynamically by the middleware provider (IAimPlugin).
public sealed class TiqPlugin : IAimPlugin
{
    public string AimName => "MMC-TIQ-V2.5";

    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new TiqAimProcessor(AimName, TiqFactory.Create(settings), ports);
}
