using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using NAudio.Wave;

using Mpai.Core;

namespace Mpai.Aims.Audio;

// Audio Object Acquisition (CAE-AOA) on Windows. Device dependency lives ONLY
// in this edge AIM. It acquires a Basic Audio Object from the microphone and
// DETERMINES its qualifier: Source = Real, the Device block, and the PCM format.
//
// NOTE: despite the class name, capture here uses NAudio's WaveInEvent (the
// classic MME waveIn API), not WasapiCapture (real WASAPI) - a naming/
// implementation mismatch worth knowing about, though not itself the cause
// of the low-recording-level issue this class works around below (that
// traced to the system's own microphone input gain, which any capture API
// would be equally subject to).
public sealed class WasapiAudioAcquisition : IAudioAcquisitionAim, IStartStopAcquisition, ILevelMeter
{
    private readonly int _sampleRate;
    private readonly int _bits;
    private readonly int _channels;

    public WasapiAudioAcquisition(int sampleRate = 16000, int bits = 16, int channels = 1)
    {
        _sampleRate = sampleRate;
        _bits = bits;
        _channels = channels;
    }

    // ---- live level meter (ILevelMeter): RMS of the latest buffer, 0..1 ----
    // Updated on every DataAvailable so a consumer (SOA's voice-activity detection)
    // can watch the microphone during capture. Reads the raw captured level, before
    // NormalizeIfQuiet runs at the end.
    public double CurrentLevel { get; private set; }

    private void UpdateLevel(byte[] buffer, int bytesRecorded)
    {
        if (bytesRecorded < 2) return;
        long sumSquares = 0; int n = 0;
        for (int i = 0; i + 1 < bytesRecorded; i += 2)
        {
            short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            sumSquares += (long)sample * sample; n++;
        }
        CurrentLevel = n > 0 ? System.Math.Sqrt((double)sumSquares / n) / 32768.0 : 0.0;
    }

    public async Task<BasicAudioObject> AcquireAsync(AcquisitionRequest request)
    {
        // The source is a microphone, so the user must be told when to speak.
        AimLog.Write("CAE-AOA-V1.0", "get ready...");
        System.Threading.Thread.Sleep(1200);

        try
        {
            Console.Beep(880, 200);
            Console.Beep(1175, 250);
        }
        catch
        {
            // no bell available; the message above is enough
        }

        AimLog.Write(
            "CAE-AOA-V1.0",
            $"SPEAK NOW - recording {request.Duration.TotalSeconds:0} seconds...");

        var wavPath = Path.Combine(Path.GetTempPath(), $"aoa_{Guid.NewGuid():N}.wav");
        var format = new WaveFormat(_sampleRate, _bits, _channels);

        using (var waveIn = new WaveInEvent { WaveFormat = format })
        {
            var writer = new WaveFileWriter(wavPath, format);
            var stopped = new TaskCompletionSource();

            waveIn.DataAvailable += (_, a) => { UpdateLevel(a.Buffer, a.BytesRecorded); writer.Write(a.Buffer, 0, a.BytesRecorded); };
            waveIn.RecordingStopped += (_, _) => { writer.Dispose(); stopped.TrySetResult(); };

            waveIn.StartRecording();
            await Task.Delay(request.Duration);
            waveIn.StopRecording();
            await stopped.Task;                     // ensure the WAV header is flushed
        }

        try
        {
            NormalizeIfQuiet(wavPath);
            var bytes = await File.ReadAllBytesAsync(wavPath);
            return BasicAudioObject.FromData(bytes, BuildQualifier());
        }
        finally
        {
            // Always, including when reading the recording failed.
            try { File.Delete(wavPath); } catch { }
        }
    }

    // ---- press-to-stop acquisition ----
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private string? _activePath;
    private TaskCompletionSource? _stopped;

    public void StartAcquire()
    {
        _activePath = Path.Combine(Path.GetTempPath(), $"aoa_{Guid.NewGuid():N}.wav");
        var format = new WaveFormat(_sampleRate, _bits, _channels);

        _writer = new WaveFileWriter(_activePath, format);
        _stopped = new TaskCompletionSource();
        ReportDevice();

        _waveIn = new WaveInEvent { WaveFormat = format };

        _waveIn.DataAvailable += (_, a) => { UpdateLevel(a.Buffer, a.BytesRecorded); _writer?.Write(a.Buffer, 0, a.BytesRecorded); };
        _waveIn.RecordingStopped += (_, _) => { _writer?.Dispose(); _stopped?.TrySetResult(); };

        _waveIn.StartRecording();
    }

    public async Task<BasicAudioObject> StopAcquireAsync()
    {
        if (_waveIn is null || _stopped is null || _activePath is null)
            throw new InvalidOperationException("StartAcquire was not called.");

        _waveIn.StopRecording();
        await _stopped.Task;                 // flush the WAV header
        _waveIn.Dispose();

        NormalizeIfQuiet(_activePath);
        var bytes = await File.ReadAllBytesAsync(_activePath);
        try { File.Delete(_activePath); } catch { }

        _waveIn = null;
        _writer = null;
        _stopped = null;
        _activePath = null;

        return BasicAudioObject.FromData(bytes, BuildQualifier());
    }

    // Boosts a recording's level if it came in quiet - traced directly to a
    // real symptom: a mic recording played back "very faint" while a known-
    // good file played "loud and clear" through the identical playback path
    // in the same app, which ruled out delivery/playback and pointed at
    // capture level specifically.
    //
    // An RMS-based version (matching perceived loudness more closely in
    // principle, since AcousticProfile.Loudness is itself a loudness
    // measure, not a peak measure) was tried and made things WORSE in a
    // direct A/B test - "barely audible... v16 was definitely better."
    // Likely cause: RMS-based scaling with peak-limiting is vulnerable to a
    // single loud transient anywhere in the recording (a click, a breath) -
    // if one moment spikes, the peak-limiting cap drags the ENTIRE boost
    // down with it, even though the actual speech needed more. Reverted to
    // this simpler, empirically-better peak-based version rather than
    // continue guessing at RMS parameters through further testing rounds.
    //
    // Deliberately one-directional: only amplifies recordings BELOW
    // triggerBelow, and only up to targetPeak - already-healthy recordings
    // are left untouched, never turned down.
    // Which microphone Windows actually handed over. WaveInEvent takes the
    // DEFAULT capture device, which is not necessarily the one in front of the
    // speaker - a webcam, a monitor, a disconnected headset - and nothing said
    // so until a recording came back as noise.
    private static void ReportDevice()
    {
        try
        {
            if (WaveInEvent.DeviceCount == 0)
            {
                Console.WriteLine("[AOA] no capture device at all.");
                return;
            }

            var capabilities = WaveInEvent.GetCapabilities(0);
            Console.WriteLine($"[AOA] recording from '{capabilities.ProductName}'" +
                              $" ({WaveInEvent.DeviceCount} device(s) available)");
        }
        catch (Exception failure)
        {
            Console.WriteLine($"[AOA] could not identify the capture device: {failure.Message}");
        }
    }    private static void NormalizeIfQuiet(string wavPath, float targetPeak = 0.85f, float triggerBelow = 0.5f)
    {
        WaveFormat format;
        var samples = new List<float>();

        using (var reader = new WaveFileReader(wavPath))
        {
            format = reader.WaveFormat;
            var sampleProvider = reader.ToSampleProvider();
            var chunk = new float[4096];
            int read;
            while ((read = sampleProvider.Read(chunk, 0, chunk.Length)) > 0)
            {
                for (var i = 0; i < read; i++) samples.Add(chunk[i]);
            }
        }

        if (samples.Count == 0) return;

        var peak = 0f;
        foreach (var s in samples) peak = Math.Max(peak, Math.Abs(s));

        // Already loud enough, or genuinely silent (nothing meaningful to
        // amplify, and amplifying noise/silence would be actively wrong) -
        // leave it alone either way.
        // Say how loud it was, and how much gain is being applied.
        //
        // This normalisation exists to rescue a quiet microphone, but it also
        // DISGUISES a microphone that heard nothing: hiss at -40 dBFS lifted to
        // -1.4 dBFS is loud hiss, and whisper answers loud hiss with "(music)"
        // or an invented sentence rather than with silence. Reporting the peak
        // turns that from a mystery into a number.
        var dbfs = peak > 0 ? 20.0 * Math.Log10(peak) : -99.0;
        Console.WriteLine($"[AOA] recorded peak {dbfs:F1} dBFS");

        if (peak > 0.001f && peak < 0.02f)
        {
            Console.WriteLine(
                "[AOA] that is close to silence - the microphone caught almost nothing. " +
                "Check that Windows is recording from the microphone you are speaking into.");
        }

        if (peak <= 0.001f || peak >= triggerBelow) return;

        Console.WriteLine($"[AOA] quiet recording: applying {targetPeak / peak:F1}x gain");

        var scale = targetPeak / peak;
        for (var i = 0; i < samples.Count; i++)
        {
            samples[i] = Math.Clamp(samples[i] * scale, -1f, 1f);
        }

        using var writer = new WaveFileWriter(wavPath, format);
        writer.WriteSamples(samples.ToArray(), 0, samples.Count);
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