using System;

using AIF.Controller;

using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Mmc.Sir;   // SpeakerEmbedder, WavReader (shared primitives, pragmatic ref)

namespace Mpai.Mmc.Esd;

// MMC-ESD-V2.5 - Entity Speech Description, as an AIF IAimProcessor.
//
// Computes the Speech Descriptors of an Entity from its Speech Object: embeds the
// speech (ECAPA-TDNN) and emits a Speech Descriptors Object (MMC-SDO) carrying
// that embedding, with a Qualifier recording the descriptor format. This is SIR's
// embed step WITHOUT the gallery match - description produces the descriptor;
// recognition (or enrolment storage) is a separate step. Enrol and recognise
// therefore share exactly one feature-extraction path.
public sealed class EsdAimProcessor : IAimProcessor
{
    // A value from TFA/V1.5/formats/SpeechDescriptorsFormats.json.
    private const string ContentFormat = "ECAPA-TDNN (192-d)";

    private readonly string _instanceId;
    private readonly SpeakerEmbedder _embedder;

    private readonly string _inPort;
    private readonly string _outPort;
    private readonly string _timePort;
    private readonly string _namePort;
    private readonly AIF.SharedStorage.ISharedStorage _store;

    public EsdAimProcessor(
        string instanceId,
        SpeakerEmbedder embedder,
        AIF.SharedStorage.ISharedStorage store,
        AimPortReader ports)
    {
        _instanceId = instanceId;
        _embedder   = embedder;
        _inPort     = ports.Input("OSD-BSO-V1.5");
        _outPort    = ports.Output("MMC-SDO-V2.5");
        _timePort   = ports.Input("OSD-STM-V1.5");      // acquisition time (OSD-STM)
        _namePort   = ports.InputOrDefault("OSD-BTO-V1.5", 1, string.Empty);   // subject name (UA-originated key), optional
        _store      = store;
    }

    public string InstanceId => _instanceId;

    public System.Threading.Tasks.Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(_inPort, out var bsoJson))
            return System.Threading.Tasks.Task.FromResult(
                Message.Error(message.MessageId, _instanceId, "no Speech Object on input port"));

        var speech = MpaiJson.FromJson<BasicSpeechObject>(bsoJson);
        if (speech is null || speech.Data.Length == 0)
            return System.Threading.Tasks.Task.FromResult(
                Message.Error(message.MessageId, _instanceId, "empty Speech Object"));

        // Embed the speech - the same path SIR uses (mono 16k -> ECAPA).
        var samples   = WavReader.ReadMono16k(speech.Data);
        var embedding = _embedder.Embed(samples);

        var sdo = SpeechDescriptorsObject.FromEmbedding(embedding, ContentFormat);

        // Stamp the acquisition time (OSD-STM) into the Speech Descriptors Object.
        SimpleTime? speechTime = null;
        if (message.Ports.TryGetValue(_timePort, out var stmJson) && !string.IsNullOrWhiteSpace(stmJson))
            speechTime = MpaiJson.FromJson<SimpleTime>(stmJson);
        if (speechTime is not null)
            sdo = new SpeechDescriptorsObject
            {
                SpeechDescriptorsObjectID   = sdo.SpeechDescriptorsObjectID,
                SpeechDescriptorsObjectTime = speechTime,
                SpeechDescriptorsData       = sdo.SpeechDescriptorsData,
                SpeechDescriptorsQualifier  = sdo.SpeechDescriptorsQualifier
            };

        // Persist the VOICE half to the gallery (Shared Storage, via the Controller API).
        if (message.Ports.TryGetValue(_namePort, out var nmJson) && !string.IsNullOrWhiteSpace(nmJson))
        {
            var name = MpaiJson.FromJson<BasicTextObject>(nmJson)?.GetText();
            if (!string.IsNullOrWhiteSpace(name))
            {
                var stJson = sdo.SpeechDescriptorsObjectTime is null ? null : MpaiJson.ToJson(sdo.SpeechDescriptorsObjectTime);
                var g = Mpai.Core.SubjectGallery.Load(_store);
                g.EnrolEmbeddings(name!, voice: embedding, speechTime: stJson);
                g.SaveSubject(_store, name!);
            }
        }

        return System.Threading.Tasks.Task.FromResult(new Message
        {
            MessageId = message.MessageId,
            MessageType = message.MessageType,
            Ports = new System.Collections.Generic.Dictionary<string, string>
            {
                [_outPort] = MpaiJson.ToJson(sdo)
            }
        });
    }
}
