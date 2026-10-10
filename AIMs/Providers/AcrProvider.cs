using System;
using System.Collections.Generic;

using AIF.Controller;
using AIF.SharedStorage;
using AIF.Store;

using Mpai.Core;
using Mpai.Paf.Efd;         // EfdAimProcessor
using Mpai.Mmc.Esd;         // EsdAimProcessor
using Mpai.Paf.Fir;         // ArcFaceRecogniser  (EFD's recogniser)
using Mpai.Mmc.Sir;         // SpeakerEmbedder    (ESD's embedder)
using Mpai.Osd.VisualScene; // ScrfdFaceDetector  (EFD's detector)
using Mpai.Paf.Psd;         // PsdAimProcessor
using Mpai.Aims.Tts;        // TtsAimProcessor, TtsFactory
using Mpai.Paf.Gfd;         // GfdAimProcessor
using Mpai.Paf.Gbd;         // GbdAimProcessor
using Mpai.Paf.Sas;         // SasAimProcessor
using Mpai.Paf.Sar;         // SarAimProcessor

namespace Mpai.Providers;

// Leaf provider for the MMC-ACR-V2.5 Module (Access Control Registration; M3245).
// The Controller builds the MMC-ACR composite from its L3; this provider supplies
// ONLY the leaf AIMs the topology names:
//   PAF-EFD - Entity Face Description   (SCRFD + ArcFace -> the face descriptors)
//   MMC-ESD - Entity Speech Description (ECAPA           -> the speech descriptors)
//   MMC-PDX, MMC-TTS, PAF-GFD, PAF-GBD, PAF-SAS - the Response and Scene Rendering leaves
// EFD and ESD use the feature extractors FIR and SIR use, so what ACR registers
// MAC recognises. They register into the Module's Shared Storage, where the User
// Agent put it: on a Service, the gallery MAC reads.
public sealed class AcrProvider : IAimProvider, IDisposable
{
    private readonly AmdStore _store;
    private ArcFaceRecogniser? _arcFace;
    private SpeakerEmbedder? _ecapa;
    private ScrfdFaceDetector? _scrfd;

    public AcrProvider(AmdStore store) => _store = store;

    public string? ImplementationOf(string aimName) => CanCreate(aimName) ? AimBinaries.Of(aimName) : null;

    public bool CanCreate(string aimName) =>
        aimName is "1PAF-EFD-V1.6-I01" or "1MMC-ESD-V2.5-I01" or "1MMC-PDX-V2.5-I01" or "1MMC-TTS-V2.5-I01" or "1PAF-GFD-V1.6-I01"
                or "1PAF-GBD-V1.6-I01" or "1PAF-SAS-V1.6-I01"
                or "1PAF-SAR-V1.6-I01";

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, ISharedStorage? storage)
        => aimName switch
        {
            "1PAF-EFD-V1.6-I01" => new EfdAimProcessor(aimName, Scrfd(settings), ArcFace(settings), Gallery(storage, aimName), AimPortReader.Load(_store, aimName)),
            "1MMC-ESD-V2.5-I01" => new EsdAimProcessor(aimName, Ecapa(settings), Gallery(storage, aimName), AimPortReader.Load(_store, aimName)),
            "1MMC-PDX-V2.5-I01" => new PsdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1MMC-TTS-V2.5-I01" => new TtsAimProcessor(aimName, TtsFactory.Create(settings), AimPortReader.Load(_store, aimName)),
            "1PAF-GFD-V1.6-I01" => new GfdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1PAF-GBD-V1.6-I01" => new GbdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1PAF-SAS-V1.6-I01" => new SasAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1PAF-SAR-V1.6-I01" => new SarAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            _ => throw new NotSupportedException($"AcrProvider does not provide '{aimName}'.")
        };

    // Registration needs somewhere to register into.
    private static ISharedStorage Gallery(ISharedStorage? storage, string aimName) =>
        storage ?? throw new InvalidOperationException($"{aimName} registers into the Module's Shared Storage, and there is none: initialise it (SharedStorageInit).");

    private ArcFaceRecogniser ArcFace(IReadOnlyDictionary<string, string> s) =>
        _arcFace ??= new ArcFaceRecogniser(Setting(s, "ArcFaceModel", MpaiPaths.Model("glintr100.onnx")));
    private SpeakerEmbedder Ecapa(IReadOnlyDictionary<string, string> s) =>
        _ecapa ??= new SpeakerEmbedder(Setting(s, "EcapaModel", MpaiPaths.Model("ecapa-tdnn.onnx")));
    private ScrfdFaceDetector Scrfd(IReadOnlyDictionary<string, string> s) =>
        _scrfd ??= new ScrfdFaceDetector(Setting(s, "ScrfdModel", MpaiPaths.Model("scrfd_10g_bnkps.onnx")));

    // A model's path, as the settings give it; a relative one is the repository's.
    private static string Setting(IReadOnlyDictionary<string, string> s, string key, string fallback) =>
        s.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)
            ? (System.IO.Path.IsPathRooted(v) ? v : System.IO.Path.Combine(MpaiPaths.Root, v))
            : fallback;

    public void Dispose() { _arcFace?.Dispose(); _ecapa?.Dispose(); _scrfd?.Dispose(); }
}
