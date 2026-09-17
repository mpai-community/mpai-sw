using System;
using System.IO;
using System.Threading.Tasks;
using NAudio.Wave;
using Mpai.Core;

namespace Mpai.Aims.Speech;

// Speech Object Delivery (MMC-SOD) device on Windows. Renders a Speech Object to
// the sound device, keeping it typed as speech throughout - the object is a Speech
// Object right up to the device, where its bytes become sound. Independent of
// Audio Object Delivery (CAE-AOD): SOD has its own delivery device. The
// Windows/NAudio dependency lives ONLY in this project, never in the portable SOD
// core - mirroring how WinmmAudioDelivery is isolated in Mpai.Cae.Aod.Windows.
//
// The temporary file it plays from is removed afterwards - delivering to a
// loudspeaker should not leave files behind.
public sealed class WinmmSpeechDelivery : ISpeechDeliveryAim
{
    public async Task DeliverAsync(BasicSpeechObject speech)
    {
        if (speech.Data.Length == 0)
        {
            AimLog.Write("MMC-SOD-V2.5", "no speech to play.");
            return;
        }

        // WHAT THE BYTES ARE, READ RATHER THAN ASSUMED. Writing the Data to a file
        // named .wav and opening it with a WAV reader works only when the Data is a
        // WAV container. Text-To-Speech produces one; a capture produces raw PCM with
        // no header, and the reader then fails with a message about the file rather
        // than about the assumption. The Speech Qualifier says which it is.
        var format    = speech.SpeechQualifier?.Format;
        var container = format?.TransportFormats?.FileFormat;
        var pcm       = format?.ContentFormats?.RawData;

        WaveStream reader;
        MemoryStream? raw = null;

        if (!string.IsNullOrWhiteSpace(container))
        {
            if (container != SpeechFileFormat.Wav)
                throw new NotSupportedException(
                    $"WinmmSpeechDelivery plays WAV or raw PCM, not '{container}'.");

            reader = new WaveFileReader(new MemoryStream(speech.Data));
        }
        else if (pcm is { SamplingFrequency: > 0, Precision: > 0 })
        {
            // Raw samples, played from what the Qualifier declares - no header
            // written, none needed.
            int rate     = (int)pcm.SamplingFrequency!.Value;
            int bits     = pcm.Precision!.Value;
            int channels =
                speech.SpeechQualifier?.Attributes?.Device?.CaptureConfiguration?.ChannelCount ?? 1;
            if (channels <= 0) channels = 1;

            raw    = new MemoryStream(speech.Data);
            reader = new RawSourceWaveStream(raw, new WaveFormat(rate, bits, channels));
        }
        else
        {
            throw new NotSupportedException(
                "The Speech Qualifier states neither a container nor a raw sample " +
                "format, so there is nothing to tell the loudspeaker what these " +
                "bytes are.");
        }

        try
        {
            AimLog.Write("MMC-SOD-V2.5", $"speaking {speech.Data.Length:N0} bytes");
            using var output = new WaveOutEvent();
            output.Init(reader);
            output.Play();
            while (output.PlaybackState == PlaybackState.Playing)
                await Task.Delay(100);
        }
        finally
        {
            reader.Dispose();
            raw?.Dispose();
        }
    }
}
