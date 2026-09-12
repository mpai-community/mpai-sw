using System;
using System.Collections.Generic;
using System.Linq;

using AIF.Controller;

using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Mmc.Sir;   // WavReader (shared audio primitive, as ESD does)

namespace Mpai.Mmc.Esi;

// MMC-ESI-V2.5 - Entity Speech Interpretation, as an AIF IAimProcessor.
//
// Receives a Basic Speech Object (OSD-BSO) and produces the Speech Personal Status
// (MMC-SPS): the Personal Status Factors carried by the speech, each a chosen
// label + Degree. Fused description and interpretation.
//
// ENGINE (Phase B, effective): reads dimensional speech affect with wav2vec2
// (audeering w2v2-L-robust-12, MSP-Podcast) - arousal, dominance, valence in ~[0,1].
// The (valence, arousal) point maps to an MPAI Emotion (MMC-EEM) label via the
// affective circumplex; dominance maps to a Social Attitude (MMC-ESA) reading.
public sealed class EsiAimProcessor : IAimProcessor
{
    private readonly string _instanceId;
    private readonly Wav2Vec2EmotionEstimator _estimator;
    private readonly string _inPort;   // OSD-BSO
    private readonly string _outPort;  // MMC-SPS

    public EsiAimProcessor(string instanceId, Wav2Vec2EmotionEstimator estimator, AimPortReader ports)
    {
        _instanceId = instanceId;
        _estimator  = estimator;
        _inPort     = ports.Input("OSD-BSO-V1.5");
        _outPort    = ports.Output("MMC-SPS-V2.5");
    }

    public string InstanceId => _instanceId;

    public System.Threading.Tasks.Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(_inPort, out var bsoJson) || string.IsNullOrWhiteSpace(bsoJson))
            return System.Threading.Tasks.Task.FromResult(
                Message.Error(message.MessageId, _instanceId, "no Basic Speech Object on input port"));

        var speech = MpaiJson.FromJson<BasicSpeechObject>(bsoJson);
        if (speech is null || speech.Data.Length == 0)
            return System.Threading.Tasks.Task.FromResult(
                Message.Error(message.MessageId, _instanceId, "empty Basic Speech Object"));

        var samples = WavReader.ReadMono16k(speech.Data);
        var affect  = _estimator.Estimate(samples);

        var sps = ToSpeechPersonalStatus(affect);

        return System.Threading.Tasks.Task.FromResult(new Message
        {
            MessageId = message.MessageId,
            MessageType = message.MessageType,
            Ports = new Dictionary<string, string> { [_outPort] = MpaiJson.ToJson(sps) }
        });
    }

    // Map dimensional affect (valence, arousal, dominance in ~[0,1]) to MPAI Factors.
    //   Emotion: the (valence, arousal) quadrant of the affective circumplex, with
    //     0.5 as the neutral centre:
    //       high valence, high arousal -> HAPPINESS/happy
    //       low  valence, high arousal -> ANGER/angry
    //       low  valence, low  arousal -> SADNESS/sad
    //       high valence, low  arousal -> CALMNESS/calm
    //     near the centre -> CALMNESS/calm (low degree).
    //   Social Attitude from dominance: clearly high -> SOCIAL DOMINANCE/CONFIDENCE
    //     (confident); clearly low -> AGGRESSION/submissive; mid -> none.
    private static SpeechPersonalStatus ToSpeechPersonalStatus(SpeechAffect a)
    {
        double v = a.Valence, ar = a.Arousal, d = a.Dominance;

        // Distance from the neutral centre drives the Emotion degree.
        double dv = v - 0.5, da = ar - 0.5;
        double intensity = Math.Clamp(2.0 * Math.Sqrt(dv * dv + da * da), 0.0, 1.0);

        FactorLabel emotionLabel =
            (dv >= 0 && da >= 0) ? FactorLabel.Of("HAPPINESS", "happy", null, intensity) :
            (dv <  0 && da >= 0) ? FactorLabel.Of("ANGER", "angry", null, intensity) :
            (dv <  0 && da <  0) ? FactorLabel.Of("SADNESS", "sad", null, intensity) :
                                   FactorLabel.Of("CALMNESS", "calm", null, intensity);

        SocialAttitude? attitude = null;
        if (d >= 0.65)      attitude = SocialAttitude.Of(FactorLabel.Of("SOCIAL DOMINANCE/CONFIDENCE", "confident", null, Math.Clamp((d - 0.5) * 2, 0, 1)));
        else if (d <= 0.35) attitude = SocialAttitude.Of(FactorLabel.Of("AGGRESSION", "submissive", null, Math.Clamp((0.5 - d) * 2, 0, 1)));

        return new SpeechPersonalStatus
        {
            SpeechEmotion        = Emotion.Of(emotionLabel),
            SpeechSocialAttitude = attitude
        };
    }
}
