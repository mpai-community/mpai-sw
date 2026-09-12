using System;
using System.Collections.Generic;

using AIF.Controller;
using AIF.Store;

using Mpai.Core;
using Mpai.Aims.Asr;   // AsrAimProcessor, AsrFactory
using Mpai.Mmc.Nlu;    // NluAimProcessor
using Mpai.Mmc.Esi;    // EsiAimProcessor, Wav2Vec2EmotionEstimator
using Mpai.Mmc.Efi;    // EfiAimProcessor, HSEmotionEstimator
using Mpai.Mmc.Psm;    // PsmAimProcessor
using Mpai.Mmc.Edp;    // EdpAimProcessor, OllamaClient
using Mpai.Paf.Psd;    // PsdAimProcessor
using Mpai.Aims.Tts;   // TtsAimProcessor, TtsFactory
using Mpai.Paf.Gfd;    // GfdAimProcessor

namespace MpdApp;

// Leaf provider for the MMC-MPD-V2.5 Module (Multimodal Personal Status-based
// Dialogue). The Controller builds MMC-MPD (and its nested PSE composite) from
// their L3s; this provider supplies ONLY the leaf AIMs the topology names:
//   MMC-ASR - speech to text (Whisper)
//   MMC-NLU - meaning + Text Personal Status
//   MMC-ESI - speech affect (wav2vec2); MMC-EFI - face affect (HSEmotion)
//   MMC-PSM - multiplex the modality Personal Statuses -> Entity Personal Status
//   MMC-EDP - affective dialogue (local LLM), memory in the running Summary
//   PAF-PSD, MMC-TTS, PAF-GFD - the Response and Scene Rendering leaves
// Gesture (MMC-GPS) is not supplied - PSM's Gesture input stays a no-op optional.
internal sealed class MpdProvider : IAimProvider, IDisposable
{
    private readonly AmdStore _store;
    private OllamaClient? _llm;
    private Wav2Vec2EmotionEstimator? _w2v2;
    private HSEmotionEstimator? _hse;

    public MpdProvider(AmdStore store) => _store = store;

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings)
        => aimName switch
        {
            "MMC-ASR-V2.5" => new AsrAimProcessor(aimName, AsrFactory.Create(settings), AimPortReader.Load(_store, aimName)),
            "MMC-NLU-V2.5" => new NluAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "MMC-ESI-V2.5" => new EsiAimProcessor(aimName, W2v2(settings), AimPortReader.Load(_store, aimName)),
            "MMC-EFI-V2.5" => new EfiAimProcessor(aimName, Hse(settings), AimPortReader.Load(_store, aimName)),
            "MMC-PSM-V2.5" => new PsmAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "MMC-EDP-V2.5" => new EdpAimProcessor(aimName, Llm(settings), AimPortReader.Load(_store, aimName)),
            "PAF-PSD-V1.6" => new PsdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "MMC-TTS-V2.5" => new TtsAimProcessor(aimName, TtsFactory.Create(settings), AimPortReader.Load(_store, aimName)),
            "PAF-GFD-V1.6" => new GfdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            _ => throw new NotSupportedException($"MpdProvider does not provide '{aimName}'.")
        };

    private OllamaClient Llm(IReadOnlyDictionary<string, string> s)
    {
        if (_llm is not null) return _llm;
        string model = s.TryGetValue("OllamaModel", out var m) && !string.IsNullOrWhiteSpace(m) ? m : "llama3.2:3b";
        return _llm = new OllamaClient(model);
    }

    private Wav2Vec2EmotionEstimator W2v2(IReadOnlyDictionary<string, string> s)
    {
        if (_w2v2 is not null) return _w2v2;
        string path = s.TryGetValue("W2v2Model", out var p) && !string.IsNullOrWhiteSpace(p)
            ? p : System.IO.Path.Combine(Mpai.Core.MpaiPaths.Root, "Models", "w2v2-emotion", "model.onnx");
        return _w2v2 = new Wav2Vec2EmotionEstimator(path);
    }

    private HSEmotionEstimator Hse(IReadOnlyDictionary<string, string> s)
    {
        if (_hse is not null) return _hse;
        string path = s.TryGetValue("HseModel", out var p) && !string.IsNullOrWhiteSpace(p)
            ? p : System.IO.Path.Combine(Mpai.Core.MpaiPaths.Root, "Models", "hsemotion_enet_b0_8_va_mtl.onnx");
        return _hse = new HSEmotionEstimator(path);
    }

    public void Dispose() { _llm?.Dispose(); _w2v2?.Dispose(); _hse?.Dispose(); }
}