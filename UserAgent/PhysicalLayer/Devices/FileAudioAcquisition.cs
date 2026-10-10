using System;
using System.IO;
using System.Threading.Tasks;

using Mpai.Core;

namespace Mpai.PhysicalLayer;

// Audio acquisition from a file - a device of the Microphone Unit.
//
// The source of an Audio Object need not be a microphone: a file and a network
// stream are equally valid sources. This implementation reads a WAV file, so a
// system can run with no capture device at all â€” headless, reproducible, and
// portable to any platform.
public sealed class FileAudioAcquisition : IAudioAcquisition
{
    private readonly string sourcePath;
    private readonly int sampleRate;
    private readonly int bits;
    private readonly int channels;

    public FileAudioAcquisition(
        string sourcePath,
        int sampleRate = 48000,
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
            "MicrophoneUnit",
            $"acquired audio: {path}");

        var data = File.ReadAllBytes(path);
        return Task.FromResult(
            BasicAudioObject.FromData(
                data,
                BuildQualifier(path, WavFormat(data))));
    }

    // The acquisition AIM determines the Qualifier: what was acquired, from
    // where, and in what format.
    //
    // The Microphone Unit acquires AUDIO here, so this is an AudioQualifier. It built a
    // SpeechQualifier because the audio one held speech's types and had nothing
    // that fitted a WAV - so an Audio Object was described in speech terms, and
    // nothing meaningful could be recorded about it.
    //
    // THE FILE SAYS WHAT IT IS. A WAV states its sampling rate, bit depth and
    // channels; those are stated, not the defaults - a 48 kHz file is not labelled
    // 16 kHz because the caller did not say. The constructor's values (48 kHz, 16
    // bit, mono by default) apply only to a file with no WAV header.
    private AudioQualifier BuildQualifier(
        string path,
        (int Rate, int Bits, int Channels)? wav)
    {
        var (sampleRate, bits, channels) = wav ?? (this.sampleRate, this.bits, this.channels);
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

    // The sampling rate, bit depth and channels a RIFF/WAVE file's "fmt " chunk
    // states, or null when the data is not a WAV.
    private static (int Rate, int Bits, int Channels)? WavFormat(byte[] data)
    {
        if (data.Length < 12 ||
            System.Text.Encoding.ASCII.GetString(data, 0, 4) != "RIFF" ||
            System.Text.Encoding.ASCII.GetString(data, 8, 4) != "WAVE") return null;
        for (int pos = 12; pos + 8 <= data.Length;)
        {
            var id = System.Text.Encoding.ASCII.GetString(data, pos, 4);
            var length = BitConverter.ToInt32(data, pos + 4);
            if (id == "fmt " && pos + 8 + 16 <= data.Length)
                return (BitConverter.ToInt32(data, pos + 12), BitConverter.ToInt16(data, pos + 22), BitConverter.ToInt16(data, pos + 10));
            if (length < 0) return null;
            pos += 8 + length + (length & 1);
        }
        return null;
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

