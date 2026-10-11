using AIF.Controller;
using AIF.SharedStorage;
using AIF.Store;

using Mpai.Aims.Asr;
using Mpai.Aims.Tts;
using Mpai.Cae.Aii;
using Mpai.Cae.Asi;
using Mpai.Cae.Qcv;
using Mpai.Core;
using Mpai.Cve.Vsi;
using Mpai.Hci.Idr;
using Mpai.Mmc.Edp;
using Mpai.Mmc.Nlu;
using Mpai.Mmc.Pmx;
using Mpai.Mmc.Sir;
using Mpai.Mmc.Spe;
using Mpai.Osd.Ava;
using Mpai.Osd.Bas;
using Mpai.Osd.Bls;
using Mpai.Osd.Bvs;
using Mpai.Osd.Vii;
using Mpai.Osd.VisualScene;
using Mpai.Paf.Fir;
using Mpai.Paf.Fpe;
using Mpai.Paf.Gfd;
using Mpai.Paf.Gbd;         // GbdAimProcessor
using Mpai.Paf.Sas;         // SasAimProcessor
using Mpai.Paf.Sar;         // SarAimProcessor
using Mpai.Paf.Psd;

namespace Mpai.Cav.Hci;

// THE SUB-AIMs OF HUMAN-CAV INTERACTION IN THE CAV (M3243 3.3): every leaf of
// 1CAV-HCI-V2.0-I01, its composites (Personal Status Extraction, Response and Scene
// Rendering) included, by the names the L3 gives them. The Controller builds each
// one when the Module starts, those the scenario gives no input as well: the engines
// they use are the ones the HCI App uses, each built once and shared.
//
// root: the repository, where the settings' paths begin.
//
// Identity: FIR and SIR match against the gallery of subjects in the Module's
// Shared Storage - none enrolled in the CAV of Stage 1. EDP keeps the passenger's
// dialogue in its Private Storage (CavDialogue).
public sealed class HciProvider(AmdStore store, string root) : IAimProvider, IDisposable
{
    public const string Hci = "1CAV-HCI-V2.0-I01";

    private ScrfdFaceDetector? scrfd;
    private ArcFaceRecogniser? arcFace;
    private SpeakerEmbedder? ecapa;
    private YoloxObjectDetector? yolox;
    private Mpai.Cae.Asi.SoundClassifier? soundAsi;
    private Mpai.Cae.Aii.SoundClassifier? soundAii;
    private Wav2Vec2EmotionEstimator? w2v2;
    private HSEmotionEstimator? hse;
    private OllamaClient? llm;
    private SubjectGallery? gallery;

    private static readonly HashSet<string> Leaves =
    [
        "1OSD-BAS-V1.5-I01", "1OSD-BVS-V1.5-I01", "1OSD-BLS-V1.5-I01", "1OSD-AVA-V1.5-I01", "1CAE-QCV-V1.0-I01", "1CAE-ASI-V2.5-I01",
        "1CAE-AII-V2.5-I01", "1CVE-VSI-V1.0-I01", "1CVE-VII-V1.0-I01", "1PAF-FIR-V1.6-I01", "1MMC-SIR-V2.5-I01", "1OSD-IDR-V1.5-I01",
        "1MMC-ASR-V2.5-I01", "1MMC-NLU-V2.5-I01", "1MMC-SPE-V2.5-I01", "1PAF-FPE-V1.6-I01", "1MMC-PMX-V2.5-I01", "1MMC-EDP-V2.5-I01",
        "1MMC-PDX-V2.5-I01", "1MMC-TTS-V2.5-I01", "1PAF-GFD-V1.6-I01", "1PAF-GBD-V1.6-I01", "1PAF-SAS-V1.6-I01", "1PAF-SAR-V1.6-I01"
    ];

    public bool CanCreate(string aimName) => Leaves.Contains(aimName);

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, ISharedStorage? storage) =>
        Create(aimName, settings, storage, null);

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> given, ISharedStorage? storage, ISharedStorage? privateStorage)
    {
        var settings = Resolved(given);
        var ports = AimPortReader.Load(store, aimName);
        return aimName switch
        {
            "1OSD-BAS-V1.5-I01" => new BasAimProcessor(aimName, ports),
            "1OSD-BVS-V1.5-I01" => new BvsAimProcessor(aimName, ports),
            "1OSD-BLS-V1.5-I01" => new BlsAimProcessor(aimName, ports),
            "1OSD-AVA-V1.5-I01" => new OsdAvaAimProcessor(aimName, ports),
            "1CAE-QCV-V1.0-I01" => new QcvAimProcessor(aimName, ports),
            "1CAE-ASI-V2.5-I01" => new CaeAsiAimProcessor(aimName, soundAsi ??= new Mpai.Cae.Asi.SoundClassifier(Setting(settings, "YamnetModel", MpaiPaths.Model("yamnet.onnx")), Setting(settings, "YamnetClassMap", MpaiPaths.Model("yamnet_class_map.csv"))), ports),
            "1CAE-AII-V2.5-I01" => new CaeAiiAimProcessor(aimName, soundAii ??= new Mpai.Cae.Aii.SoundClassifier(Setting(settings, "YamnetModel", MpaiPaths.Model("yamnet.onnx")), Setting(settings, "YamnetClassMap", MpaiPaths.Model("yamnet_class_map.csv"))), ports),
            "1CVE-VSI-V1.0-I01" => new CveVsiAimProcessor(aimName, Scrfd(settings), ports),
            "1CVE-VII-V1.0-I01" => new ViiAimProcessor(aimName, yolox ??= new YoloxObjectDetector(Setting(settings, "YoloxModel", MpaiPaths.Model("yolox_s.onnx"))), ports),
            "1PAF-FIR-V1.6-I01" => new FirAimProcessor(aimName, Scrfd(settings), arcFace ??= new ArcFaceRecogniser(Setting(settings, "ArcFaceModel", MpaiPaths.Model("glintr100.onnx"))), Gallery(storage), ports),
            "1MMC-SIR-V2.5-I01" => new SirAimProcessor(aimName, ecapa ??= new SpeakerEmbedder(Setting(settings, "EcapaModel", MpaiPaths.Model("ecapa-tdnn.onnx"))), Gallery(storage), ports),
            "1OSD-IDR-V1.5-I01" => new IdrAimProcessor(aimName, ports),
            "1MMC-ASR-V2.5-I01" => new AsrAimProcessor(aimName, AsrFactory.Create(settings), ports),
            "1MMC-NLU-V2.5-I01" => new NluAimProcessor(aimName, ports),
            "1MMC-SPE-V2.5-I01" => new SpeAimProcessor(aimName, w2v2 ??= new Wav2Vec2EmotionEstimator(Setting(settings, "W2v2Model", Path.Combine(MpaiPaths.Root, "Models", "w2v2-emotion", "model.onnx"))), ports),
            "1PAF-FPE-V1.6-I01" => new FpeAimProcessor(aimName, hse ??= new HSEmotionEstimator(Setting(settings, "HseModel", MpaiPaths.Model("hsemotion_enet_b0_8_va_mtl.onnx"))), ports),
            "1MMC-PMX-V2.5-I01" => new PmxAimProcessor(aimName, ports),
            "1MMC-EDP-V2.5-I01" => new EdpAimProcessor(aimName, llm ??= new OllamaClient(Setting(settings, "OllamaModel", "llama3.2:3b")), ports, privateStorage, persona: "the CAV"),
            "1MMC-PDX-V2.5-I01" => new PsdAimProcessor(aimName, ports),
            "1MMC-TTS-V2.5-I01" => new TtsAimProcessor(aimName, TtsFactory.Create(settings), ports),
            "1PAF-GFD-V1.6-I01" => new GfdAimProcessor(aimName, ports),
            "1PAF-GBD-V1.6-I01" => new GbdAimProcessor(aimName, ports),
            "1PAF-SAS-V1.6-I01" => new SasAimProcessor(aimName, ports),
            "1PAF-SAR-V1.6-I01" => new SarAimProcessor(aimName, ports),
            _ => throw new NotSupportedException($"the HCI provider does not build {aimName}.")
        };
    }

    private ScrfdFaceDetector Scrfd(IReadOnlyDictionary<string, string> s) =>
        scrfd ??= new ScrfdFaceDetector(Setting(s, "ScrfdModel", MpaiPaths.Model("scrfd_10g_bnkps.onnx")));

    private SubjectGallery Gallery(ISharedStorage? storage) =>
        gallery ??= storage is null ? new SubjectGallery() : SubjectGallery.Load(storage);

    // The settings, a path of the repository's (Models\...) made whole.
    private Dictionary<string, string> Resolved(IReadOnlyDictionary<string, string> settings) =>
        settings.ToDictionary(p => p.Key, p => !Path.IsPathRooted(p.Value) && (File.Exists(Path.Combine(root, p.Value)) || Directory.Exists(Path.Combine(root, p.Value)))
            ? Path.Combine(root, p.Value) : p.Value);

    private static string Setting(IReadOnlyDictionary<string, string> s, string key, string fallback) =>
        s.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    public void Dispose()
    {
        scrfd?.Dispose(); arcFace?.Dispose(); ecapa?.Dispose(); yolox?.Dispose();
        soundAsi?.Dispose(); soundAii?.Dispose(); w2v2?.Dispose(); hse?.Dispose(); llm?.Dispose();
    }
}
