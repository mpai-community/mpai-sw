using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using AIF.Controller;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Cae.Qcv;

// CAE-QCV-V1.0 - Audio Qualifier Conversion, as an AIF IAimProcessor.
//
// Converts a Basic Audio Object from one Audio Qualifier (format A) to another
// (format B), changing both the audio Data and the Audio Qualifier that describes
// it. A qualifier conversion IS a format conversion: it reads what the object's
// current qualifier declares (PCM sampling frequency, precision = bit depth, and
// channel count) and rewrites the samples - and the qualifier - to the target.
//
// It is deliberately extensible: many conversions are foreseen. For now it does the
// single conversion the Human-CAV pipeline needs - ANY PCM (rate/channels/16-bit)
// -> MONO, 16 kHz, 16-bit PCM - the format the audio identifiers (YAMNet, ECAPA)
// consume. Other source formats are declared unsupported until added.
public sealed class QcvAimProcessor : IAimProcessor
{
    private const int    TargetRate      = 16000;
    private const int    TargetPrecision = 16;      // bit depth
    private const int    TargetChannels  = 1;       // mono

    private readonly string _instanceId;
    private readonly string _inPort;    // OSD-BAO (format A)
    private readonly string _outPort;   // OSD-BAO (format B)

    public QcvAimProcessor(string instanceId, AimPortReader ports)
    {
        _instanceId = instanceId;
        _inPort     = ports.Input("OSD-BAO-V1.5");
        _outPort    = ports.Output("OSD-BAO-V1.5");
    }

    public string InstanceId => _instanceId;

    public Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(_inPort, out var json) || string.IsNullOrWhiteSpace(json))
            return Err(message, "no Basic Audio Object on input port");

        var bao = MpaiJson.FromJson<BasicAudioObject>(json);
        if (bao is null)
            return Err(message, "could not parse Basic Audio Object");

        // --- read format A from the object's qualifier ---
        var pcm = bao.AudioQualifier?.Formats?.ContentFormat?.RawData?.SampleSpace;
        if (pcm?.SamplingFrequency is null || pcm.Precision is null)
            return Err(message, "input Audio Qualifier does not declare PCM SamplingFrequency + Precision (format A unknown)");

        int    srcRate      = (int)pcm.SamplingFrequency.Value;
        int    srcPrecision = pcm.Precision.Value;                 // bit depth
        int    srcChannels  = bao.AudioQualifier?.Attributes?.Device?.CaptureConfiguration?.ChannelCount ?? 1;

        if (srcPrecision != 16)
            return Err(message, $"unsupported input precision {srcPrecision}-bit (only 16-bit PCM supported for now)");

        // --- read the audio bytes (inline base64) ---
        var inline = bao.BasicAudioObjectData.OfType<InlineAudioData>().FirstOrDefault();
        if (inline is null || string.IsNullOrWhiteSpace(inline.Data))
            return Err(message, "no inline PCM audio data on the Basic Audio Object");

        byte[] pcmBytes;
        try { pcmBytes = Convert.FromBase64String(inline.Data); }
        catch { return Err(message, "audio data is not valid base64 PCM"); }

        // --- decode 16-bit PCM -> float samples, interleaved by srcChannels ---
        int totalSamples = pcmBytes.Length / 2;
        var interleaved = new float[totalSamples];
        for (int i = 0; i < totalSamples; i++)
        {
            short s = (short)(pcmBytes[2 * i] | (pcmBytes[2 * i + 1] << 8));   // little-endian
            interleaved[i] = s / 32768f;
        }

        // --- downmix to mono (average channels) ---
        int frames = srcChannels > 0 ? interleaved.Length / srcChannels : interleaved.Length;
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float acc = 0f;
            for (int c = 0; c < srcChannels; c++) acc += interleaved[f * srcChannels + c];
            mono[f] = acc / Math.Max(srcChannels, 1);
        }

        // --- resample to 16 kHz (linear interpolation) ---
        float[] outMono = (srcRate == TargetRate) ? mono : Resample(mono, srcRate, TargetRate);

        // --- re-encode 16-bit PCM mono ---
        var outBytes = new byte[outMono.Length * 2];
        for (int i = 0; i < outMono.Length; i++)
        {
            int v = (int)Math.Round(Math.Clamp(outMono[i], -1f, 1f) * 32767f);
            outBytes[2 * i]     = (byte)(v & 0xFF);
            outBytes[2 * i + 1] = (byte)((v >> 8) & 0xFF);
        }
        string outB64 = Convert.ToBase64String(outBytes);

        // --- build format B: new Data + updated Audio Qualifier ---
        var outObj = new BasicAudioObject
        {
            Header               = bao.Header,
            MInstanceID          = bao.MInstanceID,
            UEnvironmentID       = bao.UEnvironmentID,
            BasicAudioObjectID   = bao.BasicAudioObjectID,
            BasicAudioObjectTime = bao.BasicAudioObjectTime,
            ParentObjects        = bao.ParentObjects,
            ChildObjects         = bao.ChildObjects,
            BasicAudioObjectData = new List<BasicAudioObjectDataItem> { new InlineAudioData(outB64) },
            ListenerPointOfView  = bao.ListenerPointOfView,
            BasicAudioObjectProperties = bao.BasicAudioObjectProperties,
            AudioQualifier       = WithTargetFormat(bao.AudioQualifier),
            DataXMData           = bao.DataXMData,
            DescrMetadata        = bao.DescrMetadata
        };

        return Task.FromResult(new Message
        {
            MessageId   = message.MessageId,
            MessageType = message.MessageType,
            Ports       = new Dictionary<string, string> { [_outPort] = MpaiJson.ToJson(outObj) }
        });
    }

    // Linear-interpolation resampler (adequate for classification/recognition front ends).
    private static float[] Resample(float[] input, int srcRate, int dstRate)
    {
        if (input.Length == 0) return input;
        long outLen = (long)input.Length * dstRate / srcRate;
        var outp = new float[outLen];
        double step = (double)srcRate / dstRate;
        for (long i = 0; i < outLen; i++)
        {
            double pos = i * step;
            int j = (int)pos;
            double frac = pos - j;
            float a = input[j];
            float b = (j + 1 < input.Length) ? input[j + 1] : a;
            outp[i] = (float)(a + (b - a) * frac);
        }
        return outp;
    }

    // Return the Audio Qualifier with the PCM format set to format B (mono, 16 kHz,
    // 16-bit), preserving everything else the qualifier carried.
    private static AudioQualifier WithTargetFormat(AudioQualifier? q)
    {
        var targetPcm = new Pcm { Header = "TFA-PCM-V1.5", SamplingFrequency = TargetRate, Precision = TargetPrecision };
        var attrs = q?.Attributes;
        var dev   = attrs?.Device;
        var newDev = new AudioDevice
        {
            DeviceID   = dev?.DeviceID,
            DeviceRole = dev?.DeviceRole,
            DeviceType = dev?.DeviceType,
            CaptureConfiguration = new CaptureConfiguration { ChannelCount = TargetChannels, SamplingMode = "Mono" }
        };
        return new AudioQualifier
        {
            Header             = q?.Header ?? "TFA-AUQ-V1.5",
            MInstanceID        = q?.MInstanceID,
            UEnvironmentID     = q?.UEnvironmentID,
            AudioQualifierID   = q?.AudioQualifierID ?? Guid.NewGuid().ToString(),
            AudioQualifierTime = q?.AudioQualifierTime,
            SubTypes           = q?.SubTypes,
            Formats            = new AudioFormats
            {
                ContentFormat = new AudioContentFormat { RawData = new AudioRawData { SampleSpace = targetPcm } },
                TransportFormat = q?.Formats?.TransportFormat
            },
            Attributes = new AudioAttributes { Device = newDev },
            DataXMData    = q?.DataXMData,
            DescrMetadata = q?.DescrMetadata
        };
    }

    private static Task<Message> Err(Message m, string reason)
        => Task.FromResult(Message.Error(m.MessageId, "CAE-QCV-V1.0", reason));
}
