using System;
using System.Collections.Generic;

using AIF.Controller;
using AIF.Store;

using Mpai.Core;
using Mpai.Aims.Asr;   // AsrAimProcessor, AsrFactory
using Mpai.Aims.Ttt;   // TttAimProcessor, TttFactory
using Mpai.Paf.Psd;    // PsdAimProcessor
using Mpai.Aims.Tts;   // TtsAimProcessor, TtsFactory
using Mpai.Paf.Gfd;    // GfdAimProcessor
using Mpai.Paf.Gbd;         // GbdAimProcessor
using Mpai.Paf.Sas;         // SasAimProcessor
using Mpai.Paf.Sar;         // SarAimProcessor

namespace Mpai.Providers;

// Leaf provider for the MMC-MAT-V2.5 Module (Multimodal Anonymous Translation).
// Supplies the leaf AIMs the L3 topology names:
//   MMC-ASR - speech to text (Whisper)
//   MMC-TTT - text-to-text translation (M2M100), to the Language Selector's target
//   MMC-PDX, MMC-TTS, PAF-GFD, PAF-GBD, PAF-SAS - Response and Scene Rendering (speaks in the target voice)
public sealed class MatProvider : IAimProvider, IDisposable
{
    private readonly AmdStore _store;
    public MatProvider(AmdStore store) => _store = store;

    // WHAT THIS PROVIDER CAN MAKE. A Service composed of several providers asks
    // before it builds, and says at startup which Apps it cannot run.
    // The binary that implements it: measured under Zero Trust (M3223 3.2).
    public string? ImplementationOf(string aimName) => CanCreate(aimName) ? AimBinaries.Of(aimName) : null;

    public bool CanCreate(string aimName) =>
        aimName is "1MMC-ASR-V2.5-I01" or "1MMC-TTT-V2.5-I01" or "1MMC-TTS-V2.5-I01" or "1MMC-PDX-V2.5-I01" or "1PAF-GFD-V1.6-I01"
                or "1PAF-GBD-V1.6-I01" or "1PAF-SAS-V1.6-I01"
                or "1PAF-SAR-V1.6-I01";
    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage)
        => aimName switch
        {
            "1MMC-ASR-V2.5-I01" => new AsrAimProcessor(aimName, AsrFactory.Create(settings), AimPortReader.Load(_store, aimName)),
            "1MMC-TTT-V2.5-I01" => new TttAimProcessor(aimName, TttFactory.Create(settings), AimPortReader.Load(_store, aimName)),
            "1MMC-PDX-V2.5-I01" => new PsdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1MMC-TTS-V2.5-I01" => new TtsAimProcessor(aimName, TtsFactory.Create(settings), AimPortReader.Load(_store, aimName)),
            "1PAF-GFD-V1.6-I01" => new GfdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1PAF-GBD-V1.6-I01" => new GbdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1PAF-SAS-V1.6-I01" => new SasAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1PAF-SAR-V1.6-I01" => new SarAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            _ => throw new NotSupportedException($"MatProvider does not provide '{aimName}'.")
        };

    public void Dispose() { }
}
