using System;
using System.IO;
using System.Threading.Tasks;

using Mpai.Core;

namespace Mpai.Aims.Audio;

// Audio Object Delivery (CAE-AOD) â€” file destination.
//
// A destination need not be a loudspeaker: writing the Audio Object to a file
// is delivery too. Lets a system run with no rendering device â€” headless and
// portable â€” and makes results inspectable after the fact.
public sealed class FileAudioDelivery : IAudioDeliveryAim
{
    private readonly string destinationFolder;

    public FileAudioDelivery(
        string destinationFolder)
    {
        this.destinationFolder = destinationFolder;
    }

    public Task DeliverAsync(
        BasicAudioObject audio)
    {
        // Read the AUDIO Qualifier directly. This went through a .Qualifier
        // property that presented it AS a Speech Qualifier - possible only while
        // the two were secretly one type - and asked SpeechFileFormat whether an
        // Audio Object was a WAV.
        var fileFormat =
            audio.AudioQualifier?.Formats?.TransportFormat?.FileFormats;

        if (fileFormat is not null &&
            fileFormat != AudioFileFormat.Wav)
        {
            throw new NotSupportedException(
                $"FileAudioDelivery writes WAV, not '{fileFormat}'.");
        }

        Directory.CreateDirectory(destinationFolder);

        var path =
            Path.Combine(
                destinationFolder,
                $"{audio.BasicAudioObjectID}.wav");

        File.WriteAllBytes(path, audio.Data);

        AimLog.Write(
            "CAE-AOD-V1.0",
            $"delivered {audio.Data.Length:N0} bytes -> {path}");

        return Task.CompletedTask;
    }
}

