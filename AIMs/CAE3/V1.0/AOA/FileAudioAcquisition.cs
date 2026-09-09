using System;
using System.IO;
using System.Threading.Tasks;

using Mpai.Core;

namespace Mpai.Aims.Audio;

// Audio Object Acquisition (CAE-AOA) â€” file source.
//
// The source of an Audio Object need not be a microphone: a file and a network
// stream are equally valid sources. This implementation reads a WAV file, so a
// system can run with no capture device at all â€” headless, reproducible, and
// portable to any platform.
public sealed class FileAudioAcquisition : IAudioAcquisitionAim
{
    private readonly string sourcePath;
    private readonly int sampleRate;
    private readonly int bits;
    private readonly int channels;

    public FileAudioAcquisition(
        string sourcePath,
        int sampleRate = 16000,
        int bits = 16,
        int channels = 1)
    {
        this.sourcePath = sourcePath;
        this.sampleRate = sampleRate;
        this.bits = bits;
        this.channels = channels;
    }

    public Task<BasicAudioObject> AcquireAsync(
        AcquisitionRequest request)
    {
        var path = sourcePath;

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Audio source not found: {path}",
                path);
        }

        AimLog.Write(
            "CAE-AOA-V1.0",
            $"acquired audio: {path}");

        return Task.FromResult(
            BasicAudioObject.FromData(
                File.ReadAllBytes(path),
                BuildQualifier(path)));
    }

    // The acquisition AIM determines the Qualifier: what was acquired, from
    // where, and in what format.
    //
    // CAE-AOA acquires AUDIO, so this is an AudioQualifier. It built a
    // SpeechQualifier because the audio one held speech's types and had nothing
    // that fitted a WAV - so an Audio Object was described in speech terms, and
    // nothing meaningful could be recorded about it.
    private AudioQualifier BuildQualifier(
        string path)
    {
        return new AudioQualifier
        {
            AudioQualifierID = Guid.NewGuid().ToString(),

            // WHEN THIS QUALIFIER WAS MADE. A SimpleTime segment with start and
            // end the same instant, absolute - epoch 1970 - in seconds.
            //
            // It is not GetKeyInfo.StoredAt, and not a duplicate of it: that is
            // the Repository's record of its own filing, and it stays behind if
            // the Object is exported or sent elsewhere. The Qualifier describes
            // the audio, so it travels with it and still says when it was made.
            AudioQualifierTime = SimpleTimeAt(DateTimeOffset.UtcNow),

            // SubTypes is left unset. Speech, Music, SoundEffects, Noise or
            // Mixed is not something a WAV header says, and acquisition cannot
            // know: a default would be a claim rather than a fact.

            Formats = new AudioFormats
            {
                ContentFormat = new AudioContentFormat
                {
                    RawData = new AudioRawData
                    {
                        SampleSpace = new Pcm
                        {
                            SamplingFrequency = sampleRate,

                            // Precision, not SamplePrecision: the bits used to
                            // represent a sample. Every caller wrote the bit
                            // depth into the wrong field, consistently.
                            Precision = bits
                        }
                    }
                },
                TransportFormat = new AudioTransportFormat
                {
                    FileFormats = AudioFileFormat.Wav
                }
            },

            Attributes = new AudioAttributes
            {
                Source = "Real",
                Device = new AudioDevice
                {
                    DeviceID = path,
                    DeviceRole = "Capture",
                    DeviceType = "Other",          // a file, not a microphone
                    CaptureConfiguration = new CaptureConfiguration
                    {
                        ChannelCount = channels,
                        SamplingMode = channels == 1 ? "Mono" : "Stereo"
                    }
                }
            }
        };
    }

    // A SimpleTime naming one instant: start and end the same, absolute epoch
    // (1970), in seconds. The schema requires both StartTime and EndTime;
    // TimeType true selects the 1970 epoch and TimeUnit "00" is seconds.
    private static SimpleTime SimpleTimeAt(DateTimeOffset moment)
    {
        var seconds = moment.ToUnixTimeMilliseconds() / 1000.0;

        return new SimpleTime
        {
            SimpleTimeID = Guid.NewGuid().ToString(),
            SimpleTimeData =
            {
                new TimeSegment
                {
                    FlagsByte = 1,          // bit0 = TimeType = absolute
                    StartTime = seconds,
                    EndTime   = seconds,
                    TimeType  = true,
                    TimeUnit  = "00"        // seconds
                }
            }
        };
    }
}

