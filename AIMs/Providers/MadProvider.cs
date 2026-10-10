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
using Mpai.Paf.Gbd;         // GbdAimProcessor
using Mpai.Paf.Sas;         // SasAimProcessor
using Mpai.Paf.Sar;         // SarAimProcessor

namespace Mpai.Providers;

// Leaf provider for the MMC-MAD-V2.5 Module (Multimodal Anonymous Dialogue).
// The Controller builds the MMC-MAD composite from its L3; this provider supplies
// ONLY the leaf AIMs the topology names:
//   MMC-ASR - speech to text (Whisper)
//   MMC-EDP - dialogue (local LLM via Ollama), using the running Summary as memory
//   MMC-PDX, MMC-TTS, PAF-GFD, PAF-GBD, PAF-SAS - the Response and Scene Rendering leaves
// No orchestration here; the User Agent drives the Module through the Controller.
public sealed class MadProvider : IAimProvider, IDisposable
{
    private readonly AmdStore _store;
    private OllamaClient? _llm;

    public MadProvider(AmdStore store) => _store = store;

    // WHAT THIS PROVIDER CAN MAKE. A Service composed of several providers asks
    // before it builds, and says at startup which Apps it cannot run.
    // The binary that implements it: measured under Zero Trust (M3223 3.2).
    public string? ImplementationOf(string aimName) => CanCreate(aimName) ? AimBinaries.Of(aimName) : null;

    public bool CanCreate(string aimName) =>
        aimName is "1MMC-ASR-V2.5-I01" or "1MMC-EDP-V2.5-I01" or "1MMC-TTS-V2.5-I01" or "1MMC-PDX-V2.5-I01" or "1PAF-GFD-V1.6-I01"
                or "1PAF-GBD-V1.6-I01" or "1PAF-SAS-V1.6-I01"
                or "1PAF-SAR-V1.6-I01";
    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage)
        => aimName switch
        {
            "1MMC-ASR-V2.5-I01" => new AsrAimProcessor(aimName, AsrFactory.Create(settings), AimPortReader.Load(_store, aimName)),
            "1MMC-EDP-V2.5-I01" => new EdpAimProcessor(aimName, Llm(settings), AimPortReader.Load(_store, aimName)),
            "1MMC-PDX-V2.5-I01" => new PsdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1MMC-TTS-V2.5-I01" => new TtsAimProcessor(aimName, TtsFactory.Create(settings), AimPortReader.Load(_store, aimName)),
            "1PAF-GFD-V1.6-I01" => new GfdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1PAF-GBD-V1.6-I01" => new GbdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1PAF-SAS-V1.6-I01" => new SasAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1PAF-SAR-V1.6-I01" => new SarAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            _ => throw new NotSupportedException($"MadProvider does not provide '{aimName}'.")
        };

    // One Ollama client, model from settings (default llama3.2:3b).
    private OllamaClient Llm(IReadOnlyDictionary<string, string> s)
    {
        if (_llm is not null) return _llm;
        string model = s.TryGetValue("OllamaModel", out var m) && !string.IsNullOrWhiteSpace(m) ? m : "llama3.2:3b";
        // Where Ollama is: OllamaUrl, or this machine's.
        return _llm = s.TryGetValue("OllamaUrl", out var url) && !string.IsNullOrWhiteSpace(url)
            ? new OllamaClient(model, url)
            : new OllamaClient(model);
    }

    public void Dispose() { _llm?.Dispose(); }
}
