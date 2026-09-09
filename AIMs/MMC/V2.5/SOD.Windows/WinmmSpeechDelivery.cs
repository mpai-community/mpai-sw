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

        var wavPath = Path.Combine(Path.GetTempPath(), $"sod_{Guid.NewGuid():N}.wav");
        try
        {
            await File.WriteAllBytesAsync(wavPath, speech.Data);
            AimLog.Write("MMC-SOD-V2.5", $"speaking {speech.Data.Length:N0} bytes");
            using var reader = new WaveFileReader(wavPath);
            using var output = new WaveOutEvent();
            output.Init(reader);
            output.Play();
            while (output.PlaybackState == PlaybackState.Playing)
                await Task.Delay(100);
        }
        finally
        {
            try { File.Delete(wavPath); } catch { }
        }
    }
}
