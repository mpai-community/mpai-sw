using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using AIF.Controller;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Cae.Aii;

// CAE-AII-V2.5 - Audio Instance Identification, as an AIF IAimProcessor.
//
// Receives a (generic, non-speech) Basic Audio Object and produces its Audio
// Instance Identifier (OSD-IID): what the sound IS, against the AudioSet taxonomy.
// It is the audio analogue of OSD-VII - where VII identifies a located visual
// object, AII identifies a located sound object. The audio arrives already in the
// format the model needs (mono, 16 kHz, 16-bit PCM), produced by Audio Qualifier
// Conversion (CAE-QCV); AII reads the samples directly - no decode here.
//
// Engine: YAMNet (yamnet.onnx). The highest-scoring AudioSet class becomes the
// instance identity: InstanceLabel = the class (e.g. "Siren"), LabelConfidenceLevel
// the score, taxonomy the flat ["sound", <class>]. Per the OSD-IID rule that an
// empty identification is not representable, when nothing is classified it returns
// a coarse ["sound"] candidate rather than an empty identifier.
public sealed class CaeAiiAimProcessor : IAimProcessor
{
    private const string IidHeader = "OSD-IID-V1.5";

    private readonly string _instanceId;
    private readonly SoundClassifier _classifier;
    private readonly string _inPort;    // OSD-BAO (mono-16k, from CAE-QCV)
    private readonly string _outPort;   // OSD-IID

    public CaeAiiAimProcessor(string instanceId, SoundClassifier classifier, AimPortReader ports)
    {
        _instanceId = instanceId;
        _classifier = classifier;
        _inPort     = ports.Input("OSD-BAO-V1.5");
        _outPort    = ports.Output("OSD-IID-V1.5");
    }

    public string InstanceId => _instanceId;

    public Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(_inPort, out var json) || string.IsNullOrWhiteSpace(json))
            return Task.FromResult(Message.Error(message.MessageId, _instanceId, "no Basic Audio Object on input port"));

        var bao = MpaiJson.FromJson<BasicAudioObject>(json);
        if (bao is null)
            return Task.FromResult(Message.Error(message.MessageId, _instanceId, "could not parse Basic Audio Object"));

        // The audio is mono-16k-16bit PCM (CAE-QCV normalised it): read samples directly.
        var samples = ReadMono16kSamples(bao);
        InstanceIdentifier iid;
        if (samples is { Length: > 0 })
        {
            var top = _classifier.Classify(samples).FirstOrDefault();
            iid = top is not null ? Identify(top.Label, top.Score) : Coarse();
        }
        else iid = Coarse();

        return Task.FromResult(new Message
        {
            MessageId = message.MessageId,
            MessageType = message.MessageType,
            Ports = new Dictionary<string, string> { [_outPort] = MpaiJson.ToJson(iid) }
        });
    }

    // Read 16-bit PCM samples (from the inline data QCV produced) as float[-1,1].
    private static float[]? ReadMono16kSamples(BasicAudioObject bao)
    {
        var inline = bao.BasicAudioObjectData.OfType<InlineAudioData>().FirstOrDefault();
        if (inline is null || string.IsNullOrWhiteSpace(inline.Data)) return null;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(inline.Data); } catch { return null; }
        int n = bytes.Length / 2;
        var samples = new float[n];
        for (int i = 0; i < n; i++)
        {
            short s = (short)(bytes[2 * i] | (bytes[2 * i + 1] << 8));
            samples[i] = s / 32768f;
        }
        return samples;
    }

    private static InstanceIdentifier Identify(string soundClass, float score) => new()
    {
        Header = IidHeader,
        InstanceIdentifier_ = soundClass,
        InstanceIdentifierData =
        {
            new InstanceCandidate
            {
                InstanceLabel = soundClass,
                LabelConfidenceLevel = score,
                Taxonomy = new InstanceTaxonomy { TaxonomyLevelIDs = { "sound", soundClass } }
            }
        }
    };

    private static InstanceIdentifier Coarse() => new()
    {
        Header = IidHeader,
        InstanceIdentifier_ = "sound",
        InstanceIdentifierData =
        {
            new InstanceCandidate
            {
                InstanceLabel = "sound",
                LabelConfidenceLevel = 0f,
                Taxonomy = new InstanceTaxonomy { TaxonomyLevelIDs = { "sound" } }
            }
        }
    };
}
