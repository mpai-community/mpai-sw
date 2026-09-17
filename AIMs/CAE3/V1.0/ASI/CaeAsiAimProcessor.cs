using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using AIF.Controller;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Cae.Asi;

// CAE-ASI-V2.5 - Audio Scene object Identification (the audio scanner).
//
// The front end captures AUDIO - it cannot know whether a sound is speech. ASI is
// where that is discovered: it classifies each audio object of the Basic Audio
// Scene with YAMNet and REQUALIFIES it accordingly.
//   * Speech  -> it creates a Basic Speech Object (OSD-BSO) carrying the audio
//               object's data with the appropriate Speech Qualifier (ASI, having
//               found it is speech, knows the target qualifier), and emits it to
//               Speaker Recognition / ASR / Personal Status.
//   * Other   -> it emits the Basic Audio Object (OSD-BAO) unchanged to Audio
//               Instance Identification.
// Requalification (BAO -> BSO) is the type change; the two outputs are different
// data types, so routing is by data type. The audio arrives already mono/16 kHz
// (Audio Qualifier Conversion normalised it where needed), so YAMNet reads the
// samples directly.
public sealed class CaeAsiAimProcessor : IAimProcessor
{
    private readonly string _instanceId;
    private readonly SoundClassifier _classifier;
    private readonly string _inPort;      // OSD-BAS (scene descriptors)
    private readonly string _speechPort;  // OSD-BSO -> SIR
    private readonly string _audioPort;   // OSD-BAO -> AII

    public CaeAsiAimProcessor(string instanceId, SoundClassifier classifier, AimPortReader ports)
    {
        _instanceId = instanceId;
        _classifier = classifier;
        _inPort      = ports.Input("OSD-BAS-V1.5");
        _speechPort  = ports.Output("OSD-BSO-V1.5");
        _audioPort   = ports.Output("OSD-BAO-V1.5");
    }

    public string InstanceId => _instanceId;

    public Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(_inPort, out var json) || string.IsNullOrWhiteSpace(json))
            return Task.FromResult(Message.Error(message.MessageId, _instanceId, "no Basic Audio Scene Descriptors on input port"));

        var scene = MpaiJson.FromJson<BasicAudioSceneDescriptors>(json);
        var entry = scene?.BasicAudioSceneDescriptorsEntries?.FirstOrDefault();
        var obj   = entry?.AudioObjectIDOrAudioObject;
        if (obj is null)
            return Task.FromResult(Message.Error(message.MessageId, _instanceId, "no audio object in scene"));

        var bytes = ReadInlineBytes(obj);
        bool isSpeech = false;
        if (bytes is { Length: > 0 })
        {
            var samples = PcmToFloat(bytes);
            var top = _classifier.Classify(samples).FirstOrDefault();
            isSpeech = top is not null && top.IsSpeech;
        }

        var ports = new Dictionary<string, string>();
        if (isSpeech)
        {
            // Requalify: create a Basic Speech Object from the audio object.
            var bso = new BasicSpeechObject
            {
                Header                    = "OSD-BSO-V1.5",
                MInstanceID               = obj.MInstanceID,
                UEnvironmentID            = obj.UEnvironmentID,
                BasicSpeechObjectID       = obj.BasicAudioObjectID,
                BasicSpeechObjectSpaceTime = obj.BasicAudioObjectTime,
                Data                      = bytes ?? Array.Empty<byte>(),
                SpeechQualifier           = SpeechQualifierFrom(obj.AudioQualifier)
            };
            ports[_speechPort] = MpaiJson.ToJson(bso);
        }
        else
        {
            ports[_audioPort] = MpaiJson.ToJson(obj);
        }

        return Task.FromResult(new Message
        {
            MessageId   = message.MessageId,
            MessageType = message.MessageType,
            Ports       = ports
        });
    }

    private static byte[]? ReadInlineBytes(BasicAudioObject obj)
    {
        var inline = obj.BasicAudioObjectData.OfType<InlineAudioData>().FirstOrDefault();
        if (inline is null || string.IsNullOrWhiteSpace(inline.Data)) return null;
        try { return Convert.FromBase64String(inline.Data); } catch { return null; }
    }

    private static float[] PcmToFloat(byte[] bytes)
    {
        int n = bytes.Length / 2;
        var s = new float[n];
        for (int i = 0; i < n; i++)
            s[i] = (short)(bytes[2 * i] | (bytes[2 * i + 1] << 8)) / 32768f;
        return s;
    }

    // The Speech Qualifier ASI attaches to a discovered speech object: mono, 16 kHz,
    // 16-bit PCM (the format the speech consumers use).
    // REQUALIFICATION KEEPS THE DATA, SO IT MUST DESCRIBE THE DATA IT KEPT. This
    // AIM is the one place where a medium is reclassified, and nature forces it: a
    // scene is captured as sound waves, and only here is it discovered that an
    // object is speech. The bytes are unchanged, so the sampling frequency and the
    // precision are whatever the acquisition determined - 48 kHz in a scene, not
    // the 16 kHz the recognisers happen to want.
    //
    // Asserting 16 kHz over 48 kHz samples is worse than declaring nothing: every
    // consumer that obeys the Qualifier then decodes at a third of the rate,
    // silently. A Speech Object at 48 kHz is a perfectly good Speech Object; where
    // a consumer needs another rate, CAE-QCV is composed in to convert it.
    private static SpeechQualifier SpeechQualifierFrom(AudioQualifier? audio) => new()
    {
        Header            = "TFA-SPQ-V1.5",
        SpeechQualifierID = Guid.NewGuid().ToString(),
        Format = new SpeechFormat
        {
            ContentFormats = new SpeechContentFormats
            {
                RawData = audio?.Formats?.ContentFormat?.RawData?.SampleSpace
            }
        },
        Attributes = new SpeechAttributes
        {
            Device = new AudioDevice { CaptureConfiguration = new CaptureConfiguration { ChannelCount = 1, SamplingMode = "Mono" } }
        }
    };
}
