using System;
using System.Collections.Generic;

using AIF.Controller;
using AIF.SharedStorage;
using AIF.Store;

using Mpai.Core;
using Mpai.Paf.Fir;         // FirAimProcessor, ArcFaceRecogniser
using Mpai.Mmc.Sir;         // SirAimProcessor, SpeakerEmbedder
using Mpai.Osd.Idr;         // IdrAimProcessor
using Mpai.Osd.VisualScene; // ScrfdFaceDetector
using Mpai.Paf.Psd;         // PsdAimProcessor
using Mpai.Aims.Tts;        // TtsAimProcessor, TtsFactory
using Mpai.Paf.Gfd;         // GfdAimProcessor
using Mpai.Paf.Gbd;         // GbdAimProcessor
using Mpai.Paf.Sas;         // SasAimProcessor
using Mpai.Paf.Sar;         // SarAimProcessor

namespace Mpai.Providers;

// Leaf provider for the MMC-MAC-V2.5 Module (Multimodal Access Control; M3245).
// The Controller builds the MMC-MAC composite from its L3; this provider supplies
// ONLY the leaf AIMs the topology names:
//   PAF-FIR - face recognition (SCRFD + ArcFace), against the gallery
//   MMC-SIR - speaker recognition (ECAPA), against the gallery
//   OSD-IDR - the check: the face and the voice of one registered person
//   MMC-PDX, MMC-TTS, PAF-GFD, PAF-GBD, PAF-SAS - the Response and Scene Rendering leaves
// THE GALLERY is the Module's Shared Storage, where the User Agent put it
// (SharedStorageInit): on a Service, the one ACR registers into. FIR and SIR read
// it again before each match, since it changes while the Module runs.
public sealed class MacProvider : IAimProvider, IDisposable
{
    private readonly AmdStore _store;
    private readonly SubjectGallery _gallery = new();
    private ArcFaceRecogniser? _arcFace;
    private SpeakerEmbedder? _ecapa;
    private ScrfdFaceDetector? _scrfd;

    public MacProvider(AmdStore store) => _store = store;

    public string? ImplementationOf(string aimName) => CanCreate(aimName) ? AimBinaries.Of(aimName) : null;

    public bool CanCreate(string aimName) =>
        aimName is "1PAF-FIR-V1.6-I01" or "1MMC-SIR-V2.5-I01" or "1OSD-IDR-V1.5-I01" or "1MMC-PDX-V2.5-I01" or "1MMC-TTS-V2.5-I01" or "1PAF-GFD-V1.6-I01"
                or "1PAF-GBD-V1.6-I01" or "1PAF-SAS-V1.6-I01"
                or "1PAF-SAR-V1.6-I01";

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, ISharedStorage? storage)
        => aimName switch
        {
            "1PAF-FIR-V1.6-I01" => new FirAimProcessor(aimName, Scrfd(settings), ArcFace(settings), _gallery, AimPortReader.Load(_store, aimName), storage),
            "1MMC-SIR-V2.5-I01" => new SirAimProcessor(aimName, Ecapa(settings), _gallery, AimPortReader.Load(_store, aimName), storage),
            "1OSD-IDR-V1.5-I01" => new IdrAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1MMC-PDX-V2.5-I01" => new PsdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1MMC-TTS-V2.5-I01" => new TtsAimProcessor(aimName, TtsFactory.Create(settings), AimPortReader.Load(_store, aimName)),
            "1PAF-GFD-V1.6-I01" => new GfdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1PAF-GBD-V1.6-I01" => new GbdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1PAF-SAS-V1.6-I01" => new SasAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1PAF-SAR-V1.6-I01" => new SarAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            _ => throw new NotSupportedException($"MacProvider does not provide '{aimName}'.")
        };

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
