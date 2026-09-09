using System;
using System.Collections.Generic;

using AIF.Controller;
using AIF.Store;

using Mpai.Core;
using Mpai.Paf.Efd;          // EfdAimProcessor
using Mpai.Mmc.Esd;          // EsdAimProcessor
using Mpai.Paf.Fir;          // ArcFaceRecogniser  (EFD's recogniser)
using Mpai.Mmc.Sir;          // SpeakerEmbedder    (ESD's embedder)
using Mpai.Osd.VisualScene;  // ScrfdFaceDetector  (EFD's detector)
using Mpai.Paf.Psd;          // PsdAimProcessor
using Mpai.Aims.Tts;         // TtsAimProcessor, TtsFactory
using Mpai.Paf.Gfd;          // GfdAimProcessor

namespace AcrApp;

// Leaf provider for the MMC-ACR-V2.5 Module. The Controller builds the ACR
// composite from its L3 (1MMC-ACR-V2.5-I01.json); this provider supplies ONLY
// the leaf AIMs the topology names:
//   PAF-EFD - Entity Face Description  (SCRFD + ArcFace -> Face Descriptors)
//   MMC-ESD - Entity Speech Description (ECAPA        -> Speech Descriptors)
//   PAF-PSD, MMC-TTS, PAF-GFD - the Response and Scene Rendering leaves
// EFD and ESD share exactly the feature extractors FIR/SIR use, so the
// descriptors ACR writes are comparable with what MAC reads. No orchestration
// here: the provider only constructs AIMs on the Controller's request.
internal sealed class AcrProvider : IAimProvider, IDisposable
{
    private readonly AmdStore _store;

    private ArcFaceRecogniser? _arcFace;
    private SpeakerEmbedder?   _ecapa;
    private ScrfdFaceDetector? _scrfd;

    public AcrProvider(AmdStore store) => _store = store;

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings)
        => aimName switch
        {
            "PAF-EFD-V1.6" => new EfdAimProcessor(aimName, Scrfd(settings), ArcFace(settings), AimPortReader.Load(_store, aimName)),
            "MMC-ESD-V2.5" => new EsdAimProcessor(aimName, Ecapa(settings), AimPortReader.Load(_store, aimName)),
            "PAF-PSD-V1.6" => new PsdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "MMC-TTS-V2.5" => new TtsAimProcessor(aimName, TtsFactory.Create(settings), AimPortReader.Load(_store, aimName)),
            "PAF-GFD-V1.6" => new GfdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            _ => throw new NotSupportedException($"AcrProvider does not provide '{aimName}'.")
        };

    private ArcFaceRecogniser ArcFace(IReadOnlyDictionary<string, string> s) =>
        _arcFace ??= new ArcFaceRecogniser(Setting(s, "ArcFaceModel", Mpai.Core.MpaiPaths.Model("glintr100.onnx")));
    private SpeakerEmbedder Ecapa(IReadOnlyDictionary<string, string> s) =>
        _ecapa ??= new SpeakerEmbedder(Setting(s, "EcapaModel", Mpai.Core.MpaiPaths.Model("ecapa-tdnn.onnx")));
    private ScrfdFaceDetector Scrfd(IReadOnlyDictionary<string, string> s) =>
        _scrfd ??= new ScrfdFaceDetector(Setting(s, "ScrfdModel", Mpai.Core.MpaiPaths.Model("scrfd_10g_bnkps.onnx")));

    private static string Setting(IReadOnlyDictionary<string, string> s, string key, string fallback) =>
        s.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    public void Dispose() { _arcFace?.Dispose(); _ecapa?.Dispose(); _scrfd?.Dispose(); }
}
