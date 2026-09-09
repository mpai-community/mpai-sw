using System;
using System.IO;

namespace Mpai.Mmc.Sir;

// Minimal RIFF/WAVE reader: 16-bit PCM -> mono float[] in [-1,1]. Averages
// channels to mono; asserts 16 kHz (what ECAPA expects) but does not resample.
//
// Reads from a file path, a byte[] (an in-memory WAV, e.g. BasicSpeechObject.Data),
// or any Stream - all three share one core so the parsing lives in a single place.
public static class WavReader
{
    public static float[] ReadMono16k(string path)
    {
        using var stream = File.OpenRead(path);
        return ReadMono16k(stream, Path.GetFileName(path));
    }

    // Decode an in-memory WAV (e.g. the bytes carried in a BasicSpeechObject.Data).
    public static float[] ReadMono16k(byte[] wavBytes)
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
        if (sampleRate != 16000)
            Console.WriteLine($"  WARNING: {sourceName} is {sampleRate} Hz, expected 16000.");

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
}
