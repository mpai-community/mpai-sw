using System;

using Mpai.Core;

namespace Mpai.RcaWeb.Media;

// WHAT THE BROWSER HEARD, AS A SPEECH OBJECT. The browser records at its own rate;
// rca.js turns that into 16 kHz, 16-bit mono PCM with no header - exactly what
// Speech Object Acquisition produces on the desktop - and this states it in the
// Qualifier, because raw PCM says nothing about itself.
public static class SpeechPackaging
{
    public static BasicSpeechObject FromPcm16k(byte[] pcm, string? language) =>
        BasicSpeechObject.FromData(pcm, new SpeechQualifier
        {
            SpeechQualifierID = Guid.NewGuid().ToString(),
            Format = new SpeechFormat
            {
                ContentFormats = new SpeechContentFormats
                {
                    RawData = new Pcm { SamplingFrequency = 16000, Precision = 16 }
                }
            },
            Attributes = new SpeechAttributes
            {
                Source = SpeechSource.Real,
                Metadata = new SpeechMetadata
                {
                    Language = language is null ? null
                             : new Language { LanguageCode = language, LanguageFormat = LanguageFormat.Iso639_1 },
                    SpeakerProperties = new SpeakerProperties { SpeakerType = SpeakerType.Human, SpeakerCount = 1 }
                }
            }
        });

    // How long a WAV plays: data length over byte rate, read from its header.
    public static double WavSeconds(byte[] wav)
    {
        if (wav.Length < 44 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F') return 0;
        int byteRate = BitConverter.ToInt32(wav, 28);
        if (byteRate <= 0) return 0;
        // find the "data" chunk rather than assume it sits at byte 36
        int i = 12;
        while (i + 8 <= wav.Length)
        {
            int size = BitConverter.ToInt32(wav, i + 4);
            if (wav[i] == 'd' && wav[i + 1] == 'a' && wav[i + 2] == 't' && wav[i + 3] == 'a')
                return Math.Min(size, wav.Length - i - 8) / (double)byteRate;
            i += 8 + Math.Max(0, size);
        }
        return (wav.Length - 44) / (double)byteRate;
    }
}
