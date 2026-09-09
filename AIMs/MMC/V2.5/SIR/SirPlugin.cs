using System.Collections.Generic;
using AIF.Controller;
using Mpai.Core;
using Mpai.Core.OSD;          // SubjectGallery

namespace Mpai.Mmc.Sir;

// Plug-in for Speaker Instance Recognition. Builds and caches its own ECAPA speaker
// embedder and the SubjectGallery it matches against - each path from settings,
// defaulting to the current location. Discovered dynamically (IAimPlugin).
public sealed class SirPlugin : IAimPlugin
{
    private SpeakerEmbedder? _ecapa;
    private SubjectGallery?  _gallery;

    public string AimName => "MMC-SIR-V2.5";

    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new SirAimProcessor(
            AimName,
            _ecapa   ??= new SpeakerEmbedder(Get(settings, "EcapaModel", Mpai.Core.MpaiPaths.Model("ecapa-tdnn.onnx"))),
            _gallery ??= SubjectGallery.Load(new AIF.SharedStorage.FileSharedStorage(Mpai.Core.MpaiPaths.SharedStorage, AimName, "local")),
            ports);
    private static string Get(IReadOnlyDictionary<string,string> s, string k, string d) => s.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : d;
}
