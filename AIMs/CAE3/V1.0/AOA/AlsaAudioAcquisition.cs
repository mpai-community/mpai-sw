using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

using Mpai.Core;

namespace Mpai.Aims.Audio;

// Audio Object Acquisition (CAE-AOA) on Linux, via `arecord`. Same interface and
// same determined qualifier as the Windows AOA — that is what lets the core
// (ASR-TIQ-TTS) attach to either edge without knowing which.
public sealed class AlsaAudioAcquisition : IAudioAcquisitionAim, IStartStopAcquisition
{
    private readonly int _sampleRate;
    private readonly int _bits;
    private readonly int _channels;
    private readonly string _executable;

    public AlsaAudioAcquisition(int sampleRate = 16000, int bits = 16, int channels = 1, string executable = "arecord")
    {
        _sampleRate = sampleRate;
        _bits = bits;
        _channels = channels;
        _executable = executable;
    }

    public async Task<BasicAudioObject> AcquireAsync(AcquisitionRequest request)
    {
        var wavPath = Path.Combine(Path.GetTempPath(), $"aoa_{Guid.NewGuid():N}.wav");
        var seconds = ((int)Math.Ceiling(request.Duration.TotalSeconds))
            .ToString(CultureInfo.InvariantCulture);

        var psi = new ProcessStartInfo
        {
            FileName = _executable,
            Arguments = $"-q -d {seconds} -f S16_LE -r {_sampleRate} -c {_channels} \"{wavPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using (var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start arecord."))
        {
            await p.WaitForExitAsync();
        }

        var bytes = await File.ReadAllBytesAsync(wavPath);
        try { File.Delete(wavPath); } catch { }

        return BasicAudioObject.FromData(bytes, BuildQualifier());
    }

    // ---- press-to-stop acquisition ----
    //
    // Windows had this and Linux did not, so MMC-SOA fell to its fixed-duration
    // branch and the Stop button did nothing here: recording ended by the clock.
    //
    // arecord is asked for RAW PCM on stdout rather than a WAV file, and the WAV
    // header is written here at the end. Killing a process that is writing a WAV
    // leaves the RIFF and data lengths as arecord first guessed them, because it
    // rewrites them on exit; a raw stream has no lengths to be wrong, so this
    // sidesteps signal handling altogether and needs no P/Invoke.
    private Process?     _recorder;
    private MemoryStream? _captured;
    private Task?        _pump;

    public void StartAcquire()
    {
        _captured = new MemoryStream();

        var psi = new ProcessStartInfo
        {
            FileName  = _executable,
            Arguments = $"-q -f S16_LE -r {_sampleRate} -c {_channels} -t raw",
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            CreateNoWindow         = true
        };

        _recorder = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start {_executable}.");

        // Drain continuously: a full pipe buffer would stall arecord and the
        // recording would quietly stop while appearing to continue.
        var recorder = _recorder;
        var captured = _captured;
        _pump = Task.Run(async () =>
        {
            try   { await recorder.StandardOutput.BaseStream.CopyToAsync(captured); }
            catch { /* the stream ends when the process is killed - expected */ }
        });
    }

    public async Task<BasicAudioObject> StopAcquireAsync()
    {
        if (_recorder is null || _captured is null)
            throw new InvalidOperationException("StopAcquireAsync called before StartAcquire.");

        try { if (!_recorder.HasExited) _recorder.Kill(); } catch { }

        if (_pump is not null) { try { await _pump; } catch { } }
        try { await _recorder.WaitForExitAsync(); } catch { }

        var pcm = _captured.ToArray();

        _recorder.Dispose();
        _recorder = null;
        _captured = null;
        _pump     = null;

        NormalizeIfQuiet(pcm);

        return BasicAudioObject.FromData(WrapAsWav(pcm), BuildQualifier());
    }

    // The 44-byte canonical WAV header for the PCM just captured.
    private byte[] WrapAsWav(byte[] pcm)
    {
        var blockAlign = _channels * _bits / 8;
        var byteRate   = _sampleRate * blockAlign;

        using var stream = new MemoryStream(44 + pcm.Length);
        using var writer = new BinaryWriter(stream);

        writer.Write(new[] { 'R', 'I', 'F', 'F' });
        writer.Write(36 + pcm.Length);
        writer.Write(new[] { 'W', 'A', 'V', 'E' });

        writer.Write(new[] { 'f', 'm', 't', ' ' });
        writer.Write(16);                       // PCM chunk size
        writer.Write((short)1);                 // PCM
        writer.Write((short)_channels);
        writer.Write(_sampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write((short)_bits);

        writer.Write(new[] { 'd', 'a', 't', 'a' });
        writer.Write(pcm.Length);
        writer.Write(pcm);

        writer.Flush();
        return stream.ToArray();
    }

    // In place, on 16-bit samples. Mirrors the Windows AOA: lift a quiet
    // recording towards 0.85 of full scale, and leave alone anything already
    // above 0.5 or so quiet that it is silence rather than a soft voice. A
    // microphone at a low input level otherwise reaches whisper as a whisper.
    private void NormalizeIfQuiet(byte[] pcm, float targetPeak = 0.85f, float triggerBelow = 0.5f)
    {
        if (_bits != 16 || pcm.Length < 2) return;

        var samples = pcm.Length / 2;
        var peak    = 0;

        for (var i = 0; i < samples; i++)
        {
            var value = Math.Abs(BitConverter.ToInt16(pcm, i * 2));
            if (value > peak) peak = value;
        }

        var peakFraction = peak / 32768f;
        if (peakFraction >= triggerBelow || peakFraction < 0.001f) return;

        var gain = targetPeak / peakFraction;

        for (var i = 0; i < samples; i++)
        {
            var scaled = BitConverter.ToInt16(pcm, i * 2) * gain;
            var value  = (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
            BitConverter.GetBytes(value).CopyTo(pcm, i * 2);
        }
    }
    // CAE-AOA acquires AUDIO, so it describes what it acquired with an
    // AudioQualifier. It built a SpeechQualifier because the audio one held
    // speech's types and had nothing that fitted a WAV.
    private AudioQualifier BuildQualifier() => new AudioQualifier
    {
        AudioQualifierID = Guid.NewGuid().ToString(),

        // WHEN THIS QUALIFIER WAS MADE. A SimpleTime segment with start and end the
        // same instant, absolute - epoch 1970 - in seconds.
        //
        // It is not GetKeyInfo.StoredAt, and not a duplicate of it: that is the
        // Repository's record of its own filing, and it stays behind if the Object
        // is exported or sent elsewhere. The Qualifier describes the audio, so it
        // travels with it and still says when it was made.
        AudioQualifierTime = SimpleTimeAt(DateTimeOffset.UtcNow),

        // SubTypes is left unset. Speech, Music, SoundEffects, Noise or Mixed
        // is not something a WAV header says, and acquisition cannot know: a
        // default would be a claim rather than a fact.

        Formats = new AudioFormats
        {
            ContentFormat = new AudioContentFormat
            {
                RawData = new AudioRawData
                {
                    SampleSpace = new Pcm
                    {
                        SamplingFrequency = _sampleRate,
                        Precision = _bits
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
                DeviceRole = "Capture",
                DeviceType = "Microphone",
                CaptureConfiguration = new CaptureConfiguration
                {
                    ChannelCount = _channels,
                    SamplingMode = _channels == 1 ? "Mono" : "Stereo"
                }
            }
        }
    };

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
