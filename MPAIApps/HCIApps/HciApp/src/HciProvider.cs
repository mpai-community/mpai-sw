using System;
using System.Collections.Generic;
using System.IO;

using AIF.Controller;
using AIF.Store;

using Mpai.Core;

// Front-end (scene describe / align / scan / identify)
using Mpai.Osd.Bas;         // BasAimProcessor
using Mpai.Osd.Bvs;         // BvsAimProcessor
using Mpai.Osd.Bls;         // BlsAimProcessor
using Mpai.Osd.Ava;         // OsdAvaAimProcessor
using Mpai.Cae.Qcv;         // QcvAimProcessor
using Mpai.Cae.Asi;         // CaeAsiAimProcessor, SoundClassifier
using Mpai.Cae.Aii;         // CaeAiiAimProcessor
using Mpai.Cve.Vsi;         // CveVsiAimProcessor
using Mpai.Osd.Vii;         // ViiAimProcessor
using Mpai.Osd.VisualScene; // ScrfdFaceDetector, YoloxObjectDetector

// Recognisers + reconciliation
using Mpai.Paf.Fir;         // FirAimProcessor, ArcFaceRecogniser
using Mpai.Mmc.Sir;         // SirAimProcessor, SpeakerEmbedder
using Mpai.Hci.Idr;         // IdrAimProcessor

// Dialogue + rendering
using Mpai.Mmc.Nlu;         // NluAimProcessor
using Mpai.Mmc.Esi;         // EsiAimProcessor, Wav2Vec2EmotionEstimator
using Mpai.Mmc.Efi;         // EfiAimProcessor, HSEmotionEstimator
using Mpai.Mmc.Psm;         // PsmAimProcessor
using Mpai.Mmc.Edp;         // EdpAimProcessor, OllamaClient
using Mpai.Aims.Asr;        // AsrAimProcessor, AsrFactory
using Mpai.Paf.Psd;         // PsdAimProcessor
using Mpai.Aims.Tts;        // TtsAimProcessor, TtsFactory
using Mpai.Paf.Gfd;         // GfdAimProcessor

namespace HciApp;

// Leaf provider for the MMC-HCI-V2.5 Module (Human-CAV Interaction, full
// reference). The Controller builds the HCI composite from its L3; this provider
// supplies ONLY the 18 leaf AIMs the topology names, across three groups:
//   scene front-end : BAS, BVS, BLS, AVA, QCV, ASI, VSI, AII, VII
//   recognisers     : FIR, SIR, IDR  (against the shared enrolment gallery)
//   dialogue/render : ASR, NLU, ESI, EFI, PSM, EDP, PSD, TTS, GFD
// Heavy engines are built once and shared: SCRFD (FIR + VSI), ArcFace, ECAPA,
// YOLOX (VII), the YAMNet SoundClassifier (ASI + AII), the emotion estimators
// (ESI, EFI), one Ollama client (EDP), and the SubjectGallery (FIR/SIR/IDR).
// No orchestration here; the User Agent drives the Module through the Controller.
internal sealed class HciProvider : IAimProvider, IDisposable
{
    private readonly AmdStore _store;
    private readonly SubjectGallery _gallery;

    // shared, lazily-built engines
    private ScrfdFaceDetector?        _scrfd;
    private ArcFaceRecogniser?        _arcFace;
    private SpeakerEmbedder?          _ecapa;
    private YoloxObjectDetector?      _yolox;
    private Mpai.Cae.Asi.SoundClassifier? _soundAsi;
    private Mpai.Cae.Aii.SoundClassifier? _soundAii;
    private Wav2Vec2EmotionEstimator? _w2v2;
    private HSEmotionEstimator?       _hse;
    private OllamaClient?             _llm;

    public HciProvider(AmdStore store, string galleryJsonPath)
    {
        _store = store;

        // Enrolment gallery in governed Shared Storage (scope MMC-MAC-V2.5, the
        // same gallery ACR enrols and MAC reads); one-time import of a legacy
        // gallery.json if the store is empty. FIR and SIR share this instance.
        var shared = new AIF.SharedStorage.FileSharedStorage(
            Mpai.Core.MpaiPaths.SharedStorage, "MMC-MAC-V2.5", "local");
        if (shared.MPAI_AIFM_SharedStorage_List(SubjectGallery.SubjectKeyPrefix).Count == 0 &&
            !string.IsNullOrWhiteSpace(galleryJsonPath) && File.Exists(galleryJsonPath))
        {
            SubjectGallery.Load(galleryJsonPath).Save(shared);
        }
        _gallery = SubjectGallery.Load(shared);
    }

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage)
        => aimName switch
        {
            // ---- scene front-end -------------------------------------------
            "OSD-BAS-V1.5" => new BasAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "OSD-BVS-V1.5" => new BvsAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "OSD-BLS-V1.5" => new BlsAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "OSD-AVA-V1.5" => new OsdAvaAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "CAE-QCV-V1.0" => new QcvAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "CAE-ASI-V2.5" => new CaeAsiAimProcessor(aimName, SoundAsi(settings), AimPortReader.Load(_store, aimName)),
            "CAE-AII-V2.5" => new CaeAiiAimProcessor(aimName, SoundAii(settings), AimPortReader.Load(_store, aimName)),
            "CVE-VSI-V1.0" => new CveVsiAimProcessor(aimName, Scrfd(settings), AimPortReader.Load(_store, aimName)),
            "OSD-VII-V1.5" => new ViiAimProcessor(aimName, Yolox(settings), AimPortReader.Load(_store, aimName)),

            // ---- recognisers + reconciliation ------------------------------
            "PAF-FIR-V1.6" => new FirAimProcessor(aimName, Scrfd(settings), ArcFace(settings), _gallery, AimPortReader.Load(_store, aimName)),
            "MMC-SIR-V2.5" => new SirAimProcessor(aimName, Ecapa(settings), _gallery, AimPortReader.Load(_store, aimName)),
            "OSD-IDR-V1.5" => new IdrAimProcessor(aimName, AimPortReader.Load(_store, aimName)),

            // ---- dialogue + rendering --------------------------------------
            "MMC-ASR-V2.5" => new AsrAimProcessor(aimName, AsrFactory.Create(settings), AimPortReader.Load(_store, aimName)),
            "MMC-NLU-V2.5" => new NluAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "MMC-ESI-V2.5" => new EsiAimProcessor(aimName, W2v2(settings), AimPortReader.Load(_store, aimName)),
            "MMC-EFI-V2.5" => new EfiAimProcessor(aimName, Hse(settings), AimPortReader.Load(_store, aimName)),
            "MMC-PSM-V2.5" => new PsmAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "MMC-EDP-V2.5" => new EdpAimProcessor(aimName, Llm(settings), AimPortReader.Load(_store, aimName)),
            "PAF-PSD-V1.6" => new PsdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "MMC-TTS-V2.5" => new TtsAimProcessor(aimName, TtsFactory.Create(settings), AimPortReader.Load(_store, aimName)),
            "PAF-GFD-V1.6" => new GfdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),

            _ => throw new NotSupportedException($"HciProvider does not provide '{aimName}'.")
        };

    // ---- shared engine builders (each constructed once, reused) ------------

    private ScrfdFaceDetector Scrfd(IReadOnlyDictionary<string, string> s) =>
        _scrfd ??= new ScrfdFaceDetector(Setting(s, "ScrfdModel", Mpai.Core.MpaiPaths.Model("scrfd_10g_bnkps.onnx")));

    private ArcFaceRecogniser ArcFace(IReadOnlyDictionary<string, string> s) =>
        _arcFace ??= new ArcFaceRecogniser(Setting(s, "ArcFaceModel", Mpai.Core.MpaiPaths.Model("glintr100.onnx")));

    private SpeakerEmbedder Ecapa(IReadOnlyDictionary<string, string> s) =>
        _ecapa ??= new SpeakerEmbedder(Setting(s, "EcapaModel", Mpai.Core.MpaiPaths.Model("ecapa-tdnn.onnx")));

    private YoloxObjectDetector Yolox(IReadOnlyDictionary<string, string> s) =>
        _yolox ??= new YoloxObjectDetector(Setting(s, "YoloxModel", Mpai.Core.MpaiPaths.Model("yolox_s.onnx")));

    private Mpai.Cae.Asi.SoundClassifier SoundAsi(IReadOnlyDictionary<string, string> s) =>
        _soundAsi ??= new Mpai.Cae.Asi.SoundClassifier(
            Setting(s, "YamnetModel",    Mpai.Core.MpaiPaths.Model("yamnet.onnx")),
            Setting(s, "YamnetClassMap", Mpai.Core.MpaiPaths.Model("yamnet_class_map.csv")));

    private Mpai.Cae.Aii.SoundClassifier SoundAii(IReadOnlyDictionary<string, string> s) =>
        _soundAii ??= new Mpai.Cae.Aii.SoundClassifier(
            Setting(s, "YamnetModel",    Mpai.Core.MpaiPaths.Model("yamnet.onnx")),
            Setting(s, "YamnetClassMap", Mpai.Core.MpaiPaths.Model("yamnet_class_map.csv")));

    private Wav2Vec2EmotionEstimator W2v2(IReadOnlyDictionary<string, string> s) =>
        _w2v2 ??= new Wav2Vec2EmotionEstimator(
            Setting(s, "W2v2Model", Path.Combine(Mpai.Core.MpaiPaths.Root, "Models", "w2v2-emotion", "model.onnx")));

    private HSEmotionEstimator Hse(IReadOnlyDictionary<string, string> s) =>
        _hse ??= new HSEmotionEstimator(Setting(s, "HseModel", Mpai.Core.MpaiPaths.Model("hsemotion_enet_b0_8_va_mtl.onnx")));

    private OllamaClient Llm(IReadOnlyDictionary<string, string> s)
    {
        if (_llm is not null) return _llm;
        string model = s.TryGetValue("OllamaModel", out var m) && !string.IsNullOrWhiteSpace(m) ? m : "llama3.2:3b";
        return _llm = new OllamaClient(model);
    }

    private static string Setting(IReadOnlyDictionary<string, string> s, string key, string fallback) =>
        s.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    public void Dispose()
    {
        _scrfd?.Dispose(); _arcFace?.Dispose(); _ecapa?.Dispose(); _yolox?.Dispose();
        _soundAsi?.Dispose(); _soundAii?.Dispose(); _w2v2?.Dispose(); _hse?.Dispose(); _llm?.Dispose();
    }
}
