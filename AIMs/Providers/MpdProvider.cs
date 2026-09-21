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

namespace Mpai.Providers;

// The leaf AIMs of MMC-MPD-V2.5 (Multimodal Personal Status-based Dialogue), by
// AIM Instance identifier. The Controller builds MMC-MPD and its nested PSE
// composite from their L3s; this supplies only the leaves:
//   ASR - speech to text              NLU - meaning and Text Personal Status
//   ESI - speech affect (wav2vec2)    EFI - face affect (HSEmotion)
//   PSM - multiplexes the modalities into an Entity Personal Status
//   EDP - affective dialogue (local LLM)
//   PSD, TTS, GFD - the Response and Scene Rendering leaves
// Moved from MpdApp, where it named AIMs by their Standard names.
public sealed class MpdProvider : IAimProvider, IDisposable
{
    private readonly AmdStore _store;
    private OllamaClient? _llm;
    private Wav2Vec2EmotionEstimator? _w2v2;
    private HSEmotionEstimator? _hse;

    public MpdProvider(AmdStore store) => _store = store;

    public bool CanCreate(string aimName) =>
        aimName is "1MMC-ASR-V2.5-I01" or "1MMC-NLU-V2.5-I01" or "1MMC-ESI-V2.5-I01" or "1MMC-EFI-V2.5-I01"
                or "1MMC-PSM-V2.5-I01" or "1MMC-EDP-V2.5-I01" or "1PAF-PSD-V1.6-I01" or "1MMC-TTS-V2.5-I01"
                or "1PAF-GFD-V1.6-I01";

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage)
        => aimName switch
        {
            "1MMC-ASR-V2.5-I01" => new AsrAimProcessor(aimName, AsrFactory.Create(settings), AimPortReader.Load(_store, aimName)),
            "1MMC-NLU-V2.5-I01" => new NluAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1MMC-ESI-V2.5-I01" => new EsiAimProcessor(aimName, W2v2(settings), AimPortReader.Load(_store, aimName)),
            "1MMC-EFI-V2.5-I01" => new EfiAimProcessor(aimName, Hse(settings), AimPortReader.Load(_store, aimName)),
            "1MMC-PSM-V2.5-I01" => new PsmAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1MMC-EDP-V2.5-I01" => new EdpAimProcessor(aimName, Llm(settings), AimPortReader.Load(_store, aimName)),
            "1PAF-PSD-V1.6-I01" => new PsdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1MMC-TTS-V2.5-I01" => new TtsAimProcessor(aimName, TtsFactory.Create(settings), AimPortReader.Load(_store, aimName)),
            "1PAF-GFD-V1.6-I01" => new GfdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
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
            ? MpaiPaths.Resolve(p) : System.IO.Path.Combine(MpaiPaths.Root, "Models", "w2v2-emotion", "model.onnx");
        return _w2v2 = new Wav2Vec2EmotionEstimator(path);
    }

    private HSEmotionEstimator Hse(IReadOnlyDictionary<string, string> s)
    {
        if (_hse is not null) return _hse;
        string path = s.TryGetValue("HseModel", out var p) && !string.IsNullOrWhiteSpace(p)
            ? MpaiPaths.Resolve(p) : System.IO.Path.Combine(MpaiPaths.Root, "Models", "hsemotion_enet_b0_8_va_mtl.onnx");
        return _hse = new HSEmotionEstimator(path);
    }

    public void Dispose() { _llm?.Dispose(); _w2v2?.Dispose(); _hse?.Dispose(); }
}
