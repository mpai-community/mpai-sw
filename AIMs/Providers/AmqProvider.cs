using System;
using System.Collections.Generic;

using AIF.Controller;
using AIF.Store;

using Mpai.Core;
using Mpai.Aims.Asr;   // AsrAimProcessor, AsrFactory
using Mpai.Mmc.Tiq;    // TiqAimProcessor, TiqFactory
using Mpai.Aims.Tts;   // TtsAimProcessor, TtsFactory
using Mpai.Paf.Psd;    // PsdAimProcessor
using Mpai.Paf.Gfd;    // GfdAimProcessor

namespace Mpai.Providers;

// Leaf provider for the MMC-AMQ-V2.5 Module (Answer to Multimodal Question).
// The Controller builds the MMC-AMQ composite from its L3; this provider supplies
// ONLY the leaf AIMs the topology names:
//   MMC-ASR - speech to text (Whisper)             (question, when spoken)
//   MMC-TIQ - text + image query (BLIP)            (the answer)
//   MMC-TTS - text to speech (Piper)               (the spoken answer)
//   PAF-PSD, PAF-GFD - RSR leaves, so the avatar can SPEAK with a face
// Acquisition and delivery (image, mic, speaker) are the User Agent, not sub-AIMs.
public sealed class AmqProvider : IAimProvider
{
    private readonly AmdStore _store;

    public AmqProvider(AmdStore store) => _store = store;

    // WHAT THIS PROVIDER CAN MAKE. A Service composed of several providers asks
    // before it builds, and says at startup which Apps it cannot run.
    public bool CanCreate(string aimName) =>
        aimName is "1MMC-ASR-V2.5-I01" or "1MMC-TIQ-V2.5-I01" or "1MMC-TTS-V2.5-I01" or "1PAF-PSD-V1.6-I01" or "1PAF-GFD-V1.6-I01";
    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage)
        => aimName switch
        {
            "1MMC-ASR-V2.5-I01" => new AsrAimProcessor(aimName, AsrFactory.Create(settings), AimPortReader.Load(_store, aimName)),
            "1MMC-TIQ-V2.5-I01" => new TiqAimProcessor(aimName, TiqFactory.Create(settings), AimPortReader.Load(_store, aimName)),
            "1MMC-TTS-V2.5-I01" => new TtsAimProcessor(aimName, TtsFactory.Create(settings), AimPortReader.Load(_store, aimName)),
            "1PAF-PSD-V1.6-I01" => new PsdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            "1PAF-GFD-V1.6-I01" => new GfdAimProcessor(aimName, AimPortReader.Load(_store, aimName)),
            _ => throw new NotSupportedException($"AmqProvider does not provide '{aimName}'.")
        };
}
