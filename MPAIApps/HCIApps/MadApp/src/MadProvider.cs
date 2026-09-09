using System;
using System.Collections.Generic;

using AIF.Controller;
using AIF.Store;

using Mpai.Core;
using Mpai.Aims.Asr;   // AsrAimProcessor, AsrFactory
using Mpai.Mmc.Edp;    // EdpAimProcessor, OllamaClient
using Mpai.Paf.Psd;    // PsdAimProcessor
using Mpai.Aims.Tts;   // TtsAimProcessor, TtsFactory
using Mpai.Paf.Gfd;    // GfdAimProcessor

namespace HciMad;

// Leaf provider for the MMC-MAD-V2.5 Module (Multimodal Anonymous Dialogue).
// The Controller builds the MMC-MAD composite from its L3; this provider supplies
// ONLY the leaf AIMs the topology names:
//   MMC-ASR - speech to text (Whisper)
//   MMC-EDP - dialogue (local LLM via Ollama), using the running Summary as memory
//   PAF-PSD, MMC-TTS, PAF-GFD - the Response and Scene Rendering leaves
// No orchestration here; the User Agent drives the Module through the Controller.
internal sealed class MadProvider : IAimProvider, IDisposable
{
    private readonly AmdStore _store;
    private OllamaClient? _llm;

    public MadProvider(AmdStore store) => _store = store;

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings)
        => aimName switch
        {
            "MMC-ASR-V2.5" => new AsrAimProcessor(aimName, AsrFactory.Create(settings), AimPortReader.Load(_store, aimName)),
            "MMC-EDP-V2.5" => new EdpAimProcessor(aimName, Llm(settings), AimPortReader.Load(_store, aimName)),
            "PAF-PSD-V1.6" => new PsdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "MMC-TTS-V2.5" => new TtsAimProcessor(aimName, TtsFactory.Create(settings), AimPortReader.Load(_store, aimName)),
            "PAF-GFD-V1.6" => new GfdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            _ => throw new NotSupportedException($"MadProvider does not provide '{aimName}'.")
        };

    // One Ollama client, model from settings (default llama3.2:3b).
    private OllamaClient Llm(IReadOnlyDictionary<string, string> s)
    {
        if (_llm is not null) return _llm;
        string model = s.TryGetValue("OllamaModel", out var m) && !string.IsNullOrWhiteSpace(m) ? m : "llama3.2:3b";
        return _llm = new OllamaClient(model);
    }

    public void Dispose() { _llm?.Dispose(); }
}
