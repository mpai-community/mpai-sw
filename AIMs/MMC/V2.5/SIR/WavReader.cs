using System;
using System.IO;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Mmc.Sir;

// Reads speech audio to 16 kHz mono float[] in [-1,1].
//
// A Basic Speech Object DECLARES its format in its Speech Qualifier (PCM sampling
// frequency + precision). The consumer reads by that declaration - it does not
// assume a self-describing WAV container. So the primary path decodes raw PCM per
// the qualifier. A RIFF/WAVE container is still accepted as a fallback (the
// stand-alone apps supply an in-memory WAV), by sniffing the leading "RIFF".
public static class WavReader
{
    // ---- Speech-object entry point (qualifier-driven) ----------------------

    // Decode a Basic Speech Object to 16 kHz mono. Reads the PCM format from the
    // object's Speech Qualifier; falls back to WAV parsing if the data is RIFF.
    public static float[] ReadMono16k(BasicSpeechObject speech)
    {
        var data = speech.Data ?? Array.Empty<byte>();
        if (data.Length == 0) return Array.Empty<float>();

        // Fallback: a self-describing WAV container (apps supply this).
        if (data.Length >= 4 && data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F')
            return ReadMono16k(data);

        // Primary: raw PCM described by the Speech Qualifier.
        var pcm = speech.SpeechQualifier?.Format?.ContentFormats?.RawData;
        int rate = pcm?.SamplingFrequency is double f && f > 0 ? (int)f : 16000;
        int bits = pcm?.Precision ?? 16;
        int channels = speech.SpeechQualifier?.Attributes?.Device?.CaptureConfiguration?.ChannelCount ?? 1;
        if (channels <= 0) channels = 1;

        if (bits != 16)
            throw new NotSupportedException($"Only 16-bit PCM supported (Speech Qualifier declares {bits}).");

        return DecodePcm16(data, rate, channels);
    }

    // ---- WAV container readers (fallback / files) --------------------------

    public static float[] ReadMono16k(string path)
    {
        using var stream = File.OpenRead(path);
        return ReadMono16k(stream, Path.GetFileName(path));
    }

    // PRIVATE, AND DELIBERATELY SO. This parses a RIFF container from bytes alone,
    // which is the right thing to do to a WAV and the wrong thing to do to a Speech
    // Object: the Object declares its format, and headerless PCM handed to this
    // method throws 'Not RIFF' with nothing to say why. Entity Speech Description
    // called it for weeks and the voice half of every enrolment failed silently.
    //
    // Reachable only from the Object-taking overload above, which reaches it only
    // after finding a RIFF signature. A consumer with an Object passes the Object.
    private static float[] ReadMono16k(byte[] wavBytes)
    {
        using var stream = new MemoryStream(wavBytes, writable: false);
        return ReadMono16k(stream, "<memory>");
    }

    public static float[] ReadMono16k(Stream stream, string sourceName = "<stream>")
    {
        using var br = new BinaryReader(stream);
        if (new string(br.ReadChars(4)) != "RIFF") throw new InvalidDataException("Not RIFF.");
        br.ReadInt32();
        if (new string(br.ReadChars(4)) != "WAVE") throw new InvalidDataException("Not WAVE.");

        short channels = 1, bits = 16;
        int sampleRate = 16000;
        byte[]? data = null;

        while (br.BaseStream.Position < br.BaseStream.Length)
        {
            string id = new string(br.ReadChars(4));
            int size = br.ReadInt32();
            if (id == "fmt ")
            {
                br.ReadInt16();                 // audio format
                channels = br.ReadInt16();
                sampleRate = br.ReadInt32();
                br.ReadInt32();                 // byte rate
                br.ReadInt16();                 // block align
                bits = br.ReadInt16();
                if (size > 16) br.ReadBytes(size - 16);
            }
            else if (id == "data")
            {
                data = br.ReadBytes(size);
            }
            else
            {
                br.ReadBytes(size + (size & 1)); // skip (pad to even)
            }
        }

        if (data is null) throw new InvalidDataException("No data chunk.");
        if (bits != 16) throw new NotSupportedException($"Only 16-bit PCM supported (got {bits}).");

        var mono = DecodePcm16Mono(data, channels);
        return sampleRate == 16000 ? mono : Resample(mono, sampleRate, 16000);
    }

    // ---- shared PCM helpers ------------------------------------------------

    // Raw interleaved 16-bit PCM -> 16 kHz mono float[].
    private static float[] DecodePcm16(byte[] data, int sampleRate, int channels)
    {
        var mono = DecodePcm16Mono(data, channels);
        return sampleRate == 16000 ? mono : Resample(mono, sampleRate, 16000);
    }

    private static float[] DecodePcm16Mono(byte[] data, int channels)
    {
        if (channels <= 0) channels = 1;
        int n = data.Length / 2 / channels;
        var samples = new float[n];
        for (int i = 0; i < n; i++)
        {
            int acc = 0;
            for (int c = 0; c < channels; c++)
            {
                short s = BitConverter.ToInt16(data, (i * channels + c) * 2);
                acc += s;
            }
            samples[i] = acc / channels / 32768f;
        }
        return samples;
    }

    // Simple linear resampler (adequate for ECAPA / wav2vec2 front-ends).
    private static float[] Resample(float[] input, int fromRate, int toRate)
    {
        if (fromRate == toRate || input.Length == 0) return input;
        int outLen = (int)((long)input.Length * toRate / fromRate);
        if (outLen <= 0) return Array.Empty<float>();
        var outp = new float[outLen];
        double step = (double)(input.Length - 1) / Math.Max(1, outLen - 1);
        for (int i = 0; i < outLen; i++)
        {
            double x = i * step;
            int x0 = (int)x;
            int x1 = Math.Min(x0 + 1, input.Length - 1);
            double frac = x - x0;
            outp[i] = (float)(input[x0] * (1 - frac) + input[x1] * frac);
        }
        return outp;
    }
}
