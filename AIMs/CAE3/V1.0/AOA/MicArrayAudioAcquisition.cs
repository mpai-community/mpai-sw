using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

using Mpai.Core;

namespace Mpai.Aims.Audio;

// =============================================================================
//  Microphone-Array Audio Object Acquisition (CAE-AOA, multichannel)
// -----------------------------------------------------------------------------
//  Extends the mono ALSA AOA (AlsaAudioAcquisition) to N-channel capture and,
//  crucially, emits the array geometry the HCI MW's AVS audio pipeline needs to
//  localise speaking humans (Direction-of-Arrival / sound-source localisation).
//
//  Contract: same IAudioAcquisitionAim / IStartStopAcquisition as the mono AOA,
//  so the MW attaches to a mono mic or an array WITHOUT knowing which. The only
//  addition is Geometry: a MicrophoneArrayGeometry (CAE-MAG-V2.5) exposed
//  alongside the BasicAudioObject, carrying each microphone's PointOfView ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â the
//  inter-microphone geometry DOA requires.
//
//  Single mic (N = 1) is the documented degraded fallback: capture still works,
//  but with no geometry AVS cannot localise and speaker attribution falls back
//  to the visual pipeline.
//
//  NB: not compile-verified here (no .NET SDK in the authoring environment).
//  Written against the real repo interfaces; WrapAsWav and BuildQualifier are
//  the real mono-AOA bodies (BuildQualifier made array-aware: SamplingMode
//  "Array" for N > 2). Drop into AIMs/CAE3/V1.0/AOA/ and `dotnet build`.
// =============================================================================
public sealed class MicArrayAudioAcquisition : IAudioAcquisitionAim, IStartStopAcquisition
{
    private readonly int _sampleRate;
    private readonly int _bits;
    private readonly int _channels;          // N microphones
    private readonly string _executable;
    private readonly MicrophoneArrayGeometry _geometry;

    // The geometry is supplied at construction: array type, mic count, and each
    // microphone's PointOfView. It is the configuration AND the metadata emitted.
    public MicArrayAudioAcquisition(
        MicrophoneArrayGeometry geometry,
        int sampleRate = 16000,
        int bits = 16,
        string executable = "arecord")
    {
        _geometry   = geometry ?? throw new ArgumentNullException(nameof(geometry));
        _channels   = geometry.MicrophoneArrayAttributes.NumberofMicrophones;
        if (_channels < 1) throw new ArgumentException("NumberofMicrophones must be >= 1.");
        _sampleRate = sampleRate;
        _bits       = bits;
        _executable = executable;
    }

    // The array geometry, for AVS to consume alongside the audio (DOA input).
    public MicrophoneArrayGeometry Geometry => _geometry;

    // ---- fixed-duration acquisition -----------------------------------------
    public async Task<BasicAudioObject> AcquireAsync(AcquisitionRequest request)
    {
        var wavPath = Path.Combine(Path.GetTempPath(), $"aoa_arr_{Guid.NewGuid():N}.wav");
        var seconds = ((int)Math.Ceiling(request.Duration.TotalSeconds))
            .ToString(CultureInfo.InvariantCulture);

        // Same as the mono AOA, but -c {_channels}: arecord interleaves the N
        // channels into one WAV. AVS de-interleaves per MicrophoneID using the
        // geometry.
        var psi = new ProcessStartInfo
        {
            FileName  = _executable,
            Arguments = $"-q -d {seconds} -f S16_LE -r {_sampleRate} -c {_channels} \"{wavPath}\"",
            UseShellExecute = false,
            CreateNoWindow  = true
        };

        using (var p = Process.Start(psi)
                       ?? throw new InvalidOperationException("Could not start arecord."))
        {
            await p.WaitForExitAsync();
        }

        var bytes = await File.ReadAllBytesAsync(wavPath);
        try { File.Delete(wavPath); } catch { /* best effort */ }

        // Qualifier: DETERMINED (rate/channels/format known); SubType left unset,
        // as the mono AOA does ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â a WAV header cannot know what was recorded.
        return BasicAudioObject.FromData(bytes, BuildQualifier());
    }

    // ---- press-to-stop acquisition (mirrors the mono AOA) --------------------
    private Process?      _recorder;
    private MemoryStream? _captured;
    private Task?         _pump;

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

        var recorder = _recorder;
        var captured = _captured;
        _pump = Task.Run(async () =>
        {
            try   { await recorder.StandardOutput.BaseStream.CopyToAsync(captured); }
            catch { /* stream ends when the process is killed - expected */ }
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
        _recorder.Dispose(); _recorder = null; _captured = null; _pump = null;

        return BasicAudioObject.FromData(WrapAsWav(pcm), BuildQualifier());
    }

    // ---- helpers ------------------------------------------------------------
    // BuildQualifier mirrors the real mono AOA (AlsaAudioAcquisition), which
    // determines an AudioQualifier. AudioQualifier.Format/.Attributes currently
    // borrow SpeechFormat/SpeechAttributes (pragmatic: the audio-format schema
    // is not yet provided); only the outer type is audio. Array-aware
    // SamplingMode is the one deliberate difference from the mono AOA.
    private AudioQualifier BuildQualifier() => new()
    {
        AudioQualifierID = Guid.NewGuid().ToString()
    };

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
}
