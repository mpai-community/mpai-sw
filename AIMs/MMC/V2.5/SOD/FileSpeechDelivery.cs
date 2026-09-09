using System;
using System.IO;
using System.Threading.Tasks;

using Mpai.Core;

namespace Mpai.Aims.Speech;

// Speech Object Delivery (MMC-SOD) - file destination.
//
// The speech sibling of FileAudioDelivery, and independent of it: a destination
// need not be a loudspeaker, and writing the Speech Object to a file is delivery
// too. This is what lets MMC-SOD run headless - on a server with no sound device,
// and on Linux, where the Windows/NAudio path does not exist.
//
// The object stays typed as speech to the very end. Nothing here demotes it to an
// Audio Object to reach a device.
public sealed class FileSpeechDelivery : ISpeechDeliveryAim
{
    private readonly string destinationFolder;

    public FileSpeechDelivery(
        string destinationFolder)
    {
        this.destinationFolder = destinationFolder;
    }

    public Task DeliverAsync(
        BasicSpeechObject speech)
    {
        if (speech.Data.Length == 0)
        {
            AimLog.Write("MMC-SOD-V2.5", "no speech to deliver.");
            return Task.CompletedTask;
        }

        // The SPEECH Qualifier, read directly. SpeechFileFormat, not
        // AudioFileFormat: the two carry the same value for WAV today, and are
        // still not the same thing.
        var fileFormat =
            speech.SpeechQualifier?.Format?.TransportFormats?.FileFormat;

        if (fileFormat is not null &&
            fileFormat != SpeechFileFormat.Wav)
        {
            throw new NotSupportedException(
                $"FileSpeechDelivery writes WAV, not '{fileFormat}'.");
        }

        Directory.CreateDirectory(destinationFolder);

        var path =
            Path.Combine(
                destinationFolder,
                $"{speech.BasicSpeechObjectID}.wav");

        File.WriteAllBytes(path, speech.Data);

        AimLog.Write(
            "MMC-SOD-V2.5",
            $"delivered {speech.Data.Length:N0} bytes -> {path}");

        return Task.CompletedTask;
    }
}