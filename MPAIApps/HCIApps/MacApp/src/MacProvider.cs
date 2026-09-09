using System;
using System.Collections.Generic;
using System.IO;

using AIF.Controller;
using AIF.Store;

using Mpai.Core;
using Mpai.Paf.Fir;        // FirAimProcessor, ArcFaceRecogniser
using Mpai.Mmc.Sir;        // SirAimProcessor, SpeakerEmbedder
using Mpai.Osd.Idr;        // IdrAimProcessor
using Mpai.Osd.VisualScene;// ScrfdFaceDetector
using Mpai.Paf.Psd;        // PsdAimProcessor
using Mpai.Paf.Gfd;        // GfdAimProcessor
using Mpai.Aims.Tts;       // TtsAimProcessor, TtsFactory

namespace HciMac;

// Leaf provider for the MMC-MAC-V2.5 Module. The Controller builds the MMC-MAC
// composite from its L3 (1MMC-MAC-V2.5-I01.json); this provider supplies ONLY
// the leaf AIMs the topology names:
//   PAF-FIR - face recognition (SCRFD + ArcFace, against the shared gallery)
//   MMC-SIR - speaker recognition (ECAPA, against the shared gallery)
//   OSD-IDR - reconcile the two IIDs; issue UserID, Personal Status, Response
//   PAF-PSD, MMC-TTS, PAF-GFD - the Response and Scene Rendering leaves
// The gallery lives in governed AIF Shared Storage (shared by FIR and SIR). This
// provider holds NO orchestration: it only constructs AIMs on the Controller's
// request. The User Agent drives the Module through the Controller.
internal sealed class MacProvider : IAimProvider, IDisposable
{
    private readonly AmdStore _store;
    private readonly SubjectGallery _gallery;

    private ArcFaceRecogniser? _arcFace;
    private SpeakerEmbedder?   _ecapa;
    private ScrfdFaceDetector? _scrfd;

    public MacProvider(AmdStore store, string galleryJsonPath)
    {
        _store = store;

        // Gallery in governed Shared Storage; one-time import of a legacy
        // gallery.json if the store is empty. FIR and SIR share this instance.
        var shared = new AIF.SharedStorage.FileSharedStorage(
            Mpai.Core.MpaiPaths.SharedStorage, "MMC-MAC-V2.5", "local");
        if (shared.List(SubjectGallery.SubjectKeyPrefix).Count == 0 &&
            !string.IsNullOrWhiteSpace(galleryJsonPath) && File.Exists(galleryJsonPath))
        {
            SubjectGallery.Load(galleryJsonPath).Save(shared);
        }
        _gallery = SubjectGallery.Load(shared);
    }

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings)
        => aimName switch
        {
            "PAF-FIR-V1.6" => new FirAimProcessor(aimName, Scrfd(settings), ArcFace(settings), _gallery, AimPortReader.Load(_store, aimName)),
            "MMC-SIR-V2.5" => new SirAimProcessor(aimName, Ecapa(settings), _gallery, AimPortReader.Load(_store, aimName)),
            "OSD-IDR-V1.5" => new IdrAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "PAF-PSD-V1.6" => new PsdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "MMC-TTS-V2.5" => new TtsAimProcessor(aimName, TtsFactory.Create(settings), AimPortReader.Load(_store, aimName)),
            "PAF-GFD-V1.6" => new GfdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            _ => throw new NotSupportedException($"MacProvider does not provide '{aimName}'.")
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
