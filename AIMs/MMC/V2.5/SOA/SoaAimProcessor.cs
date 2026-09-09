using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;
using Mpai.Core;

namespace Mpai.Aims.Speech;

// MMC-SOA-V2.5 ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â Speech Object Acquisition. Self-contained IAimProcessor.
// Reads its own port names from 1MMC-SOA-V2.5-I01.json at startup.
//
// The physical acquisition is identical to AOA (capturing sound waves is
// acoustic regardless of content); SOA differs only in that it produces a
// Basic SPEECH Object (OSD-SPO-V1.5) rather than a Basic Audio Object. It
// therefore reuses the same IAudioAcquisitionAim device capture and re-wraps
// the captured bytes as speech.
//
// Zero-trust input handling mirrors AOA: if a Speech Object was piped to the
// input port, SOA uses it; otherwise it acquires from the device.
public sealed class SoaAimProcessor : IAimProcessor
{
    private readonly string                 _inputPort;
    private readonly string                 _outputPort;
    private readonly IAudioAcquisitionAim   _aoa;
    private readonly IStartStopAcquisition? _startStop;
    private readonly System.TimeSpan        _duration;
    private readonly bool                   _pressToStop;
    private readonly bool                   _vadAutoStop;
    private readonly ILevelMeter?           _levelMeter;

    public string InstanceId { get; }

    public SoaAimProcessor(
        string               instanceId,
        IAudioAcquisitionAim aoa,
        AimPortReader             ports,
        System.TimeSpan?     duration = null,
        bool                 pressToStop = false,
        bool                 vadAutoStop = false)
    {
        InstanceId  = instanceId;
        _aoa        = aoa;
        _startStop  = aoa as IStartStopAcquisition;
        _levelMeter = aoa as ILevelMeter;
        _duration    = duration ?? System.TimeSpan.FromSeconds(5);
        _pressToStop = pressToStop;
        _vadAutoStop = vadAutoStop;
        _inputPort  = ports.InputOrDefault("OSD-SPO-V1.5", "InputSpeech");
        _outputPort = ports.Output("OSD-SPO-V1.5");
    }

    // Voice-activity-detection capture: record until the speaker finishes. Watches
    // the microphone level (ILevelMeter): waits for speech to start (level rises
    // above a threshold; gives up after a start timeout if nobody speaks), then
    // stops when the level stays below a threshold for a short hangover (~1.2s),
    // meaning the utterance has ended. A runaway guard caps the whole capture. This
    // is the "just speak, no press-stop" mode - the voice-activity detection the
    // acquisition assertion invited. Thresholds are approximate (energy VAD) and
    // tunable.
    private async Task<BasicAudioObject> CaptureWithVadAsync()
    {
        const double speakThreshold   = 0.02;   // RMS 0..1: above this = speaking
        const double silenceThreshold = 0.015;  // below this (held) = silence
        var  silenceHangover = System.TimeSpan.FromMilliseconds(1200);
        var  startTimeout    = System.TimeSpan.FromSeconds(8);
        var  runawayGuard    = System.TimeSpan.FromSeconds(30);

        System.Console.WriteLine("[MMC-SOA-V2.5] listening - speak; it stops when you finish.");
        _startStop!.StartAcquire();

        var start = System.DateTime.UtcNow;
        bool speechStarted = false;
        System.DateTime? silenceSince = null;
        string stopReason = "?"; int poll = 0; double maxLevel = 0;
        try { System.IO.File.AppendAllText(@"D:\AI\hci-diag.log", System.DateTime.Now.ToString("HH:mm:ss.fff") + "  " + ("AUDIO VAD enter: speak=" + speakThreshold + " silence=" + silenceThreshold + " hangover=" + silenceHangover.TotalMilliseconds + "ms") + "\n"); } catch {}

        while (true)
        {
            await Task.Delay(25);
            double level = _levelMeter!.CurrentLevel;
            var now = System.DateTime.UtcNow;
            poll++; if (level > maxLevel) maxLevel = level;
            if (poll % 8 == 0) try { System.IO.File.AppendAllText(@"D:\AI\hci-diag.log", System.DateTime.Now.ToString("HH:mm:ss.fff") + "  " + ("AUDIO poll t=" + (now-start).TotalMilliseconds.ToString("F0") + "ms level=" + level.ToString("F4") + " started=" + speechStarted) + "\n"); } catch {}

            if (now - start > runawayGuard) { stopReason = "runaway"; break; }

            if (!speechStarted)
            {
                if (level > speakThreshold) { speechStarted = true; try { System.IO.File.AppendAllText(@"D:\AI\hci-diag.log", System.DateTime.Now.ToString("HH:mm:ss.fff") + "  " + ("AUDIO SPEECH STARTED t=" + (now-start).TotalMilliseconds.ToString("F0") + "ms level=" + level.ToString("F4")) + "\n"); } catch {} }
                else if (now - start > startTimeout) { stopReason = "start-timeout-nobody-spoke"; break; }
            }
            else
            {
                if (level < silenceThreshold) { silenceSince ??= now; if (now - silenceSince.Value >= silenceHangover) { stopReason = "silence-hangover"; break; } }
                else silenceSince = null;
            }
        }

        var audio = await _startStop.StopAcquireAsync();
        try { System.IO.File.AppendAllText(@"D:\AI\hci-diag.log", System.DateTime.Now.ToString("HH:mm:ss.fff") + "  " + ("AUDIO STOP reason=" + stopReason + " elapsed=" + (System.DateTime.UtcNow-start).TotalSeconds.ToString("F2") + "s bytes=" + audio.Data.Length + " maxLevel=" + maxLevel.ToString("F4")) + "\n"); } catch {}
        System.Console.WriteLine($"[MMC-SOA-V2.5] captured {audio.Data.Length:N0} bytes (VAD)");
        return audio;
    }

    public async Task<Message> ProcessAsync(Message message)
    {
        // Use a piped speech input if the Controller delivered one WITH DATA.
        //
        // An EMPTY Speech Object is not nothing: it is how a caller asks this AIM
        // to acquire from the device, without needing an extra port. Omitting the
        // port altogether leaves the AIM with no input at all, and the Controller
        // skips it - which is how a text-only request keeps the speech branch idle.
        var supplied = message.Ports.TryGetValue(_inputPort, out var suppliedJson) &&
                       !string.IsNullOrWhiteSpace(suppliedJson)
                           ? MpaiJson.FromJson<BasicSpeechObject>(suppliedJson)
                           : null;

        if (supplied is not null && supplied.Data.Length > 0)
        {
            return new Message
            {
                MessageId   = message.MessageId,
                MessageType = "BasicSpeechObject",
                DataType    = "OSD-SPO-V1.5",
                // suppliedJson is provably non-null here: 'supplied' was parsed
                // from it. The compiler lost that when the test moved from the
                // string to the parsed object, hence the two CS8601 warnings.
                Payload     = suppliedJson!,
                Ports       = new Dictionary<string, string> { [_outputPort] = suppliedJson! }
            };
        }

        // No input delivered ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â acquire fresh from the device (same as AOA).
        var context = message.Context;
        BasicAudioObject audio;

        // VAD auto-stop mode: record until the speaker finishes (no manual stop).
        // Requires a start/stop device that also meters its level.
        try { System.IO.File.AppendAllText(@"D:\AI\hci-diag.log", System.DateTime.Now.ToString("HH:mm:ss.fff") + "  " + ("AUDIO branch: vadAutoStop=" + _vadAutoStop + " startStop=" + (_startStop is not null) + " levelMeter=" + (_levelMeter is not null)) + "\n"); } catch {}
        if (_vadAutoStop && _startStop is not null && _levelMeter is not null)
        {
            try { System.IO.File.AppendAllText(@"D:\AI\hci-diag.log", System.DateTime.Now.ToString("HH:mm:ss.fff") + "  " + ("AUDIO -> VAD branch") + "\n"); } catch {}
            audio = await CaptureWithVadAsync();
        }
        else
        {

        // Press-to-stop is left switched off. It waits for the Stop token, and the
        // AIF Basic API gives the User Agent no way to signal a running AIM -
        // only Pause and Stop, which end the run. So it would wait for ever.
        // Fixed-duration capture is used instead; restoring press-to-stop needs a
        // UA-to-AIM signal that does not exist yet.
        // Press-to-stop, driven by PAUSE rather than by Stop.
        //
        // Stop is the wrong signal: it ends the Module, so the recording would be
        // captured and then thrown away with the run. Pause is the right one -
        // the User Agent says "that is enough" and the pipeline carries on - and
        // it is what MPAI_AIFU_MODULE_Pause and _Resume are for.
        //
        // The PauseRequests COUNT is watched, not the gate: a Pause followed
        // promptly by a Resume can open and shut the gate between two polls,
        // while the count cannot be missed. _duration is then a safety net rather
        // than the interaction - if nobody ever says stop, recording ends anyway.
        if (_startStop is not null && context.CanBePaused)
        {
            // _duration belongs to the OTHER branch: a device that cannot be
            // interrupted has to stop by the clock, so five seconds is its whole
            // window. Here the CALLER says when to stop, and a five second limit
            // was cutting people off mid-sentence for no reason.
            //
            // A limit still has to exist, but its job changes: it is now only a
            // runaway guard, so that a microphone nobody stops does not record
            // for ever. Five minutes is far longer than any sentence and far
            // shorter than a forgotten session.
            var runawayGuard    = System.TimeSpan.FromMinutes(5);
            var requestsAtStart = context.PauseRequests;
            var deadline        = System.DateTime.UtcNow + runawayGuard;

            System.Console.WriteLine(
                "[MMC-SOA-V2.5] recording - speak after the beep, then press Stop " +
                $"(gives up after {runawayGuard.TotalMinutes:N0} minutes if nobody does)");
            // Console.Beep is Windows-only; the guard silences CA1416 and keeps
            // the catch for a console that has no beeper attached.
            if (System.OperatingSystem.IsWindows())
            {
                try { System.Console.Beep(880, 200); } catch { /* no beeper */ }
            }

            _startStop.StartAcquire();

            while (context.PauseRequests == requestsAtStart &&
                   !context.StopToken.IsCancellationRequested &&
                   System.DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
            }

            audio = await _startStop.StopAcquireAsync();

            if (System.OperatingSystem.IsWindows())
            {
                try { System.Console.Beep(440, 150); } catch { /* no beeper */ }
            }
            System.Console.WriteLine(
                $"[MMC-SOA-V2.5] captured {audio.Data.Length:N0} bytes");

            // Honour the pause itself: if the User Agent has not resumed yet,
            // wait here rather than running on while the Module is paused.
            await context.CheckAsync();
        }
        else
        {
            audio = await _aoa.AcquireAsync(new AcquisitionRequest { Duration = _duration });
        }
        }

        // Re-interpret the captured audio as a Basic Speech Object (OSD-SPO).
        //
        // The trigger object's Qualifier is carried onto what was captured. It is
        // the only thing that knows what language is about to be spoken: the
        // microphone cannot say, and MMC-ASR reads the language from the Speech
        // Qualifier - which is why MMC-TST has no Input Language Selector. Without
        // this, live speech reached ASR unlabelled, ASR fell back to its
        // configured default, and Italian speech came back as
        // "(speaking in foreign language)".
        var requestQualifier = supplied?.SpeechQualifier;

        // The claim, made explicitly.
        //
        // A microphone yields an AUDIO Object and has no idea whether it caught
        // speech, music or a door closing. Asserting that it is SPEECH is what
        // this AIM is FOR - and the assertion belongs in the Qualifier, where
        // MMC-ASR and everything downstream can see it, rather than being implied
        // by the Data Type alone.
        //
        //   inherit    Language, from the Speech Qualifier on the request: only
        //              the caller knows what is about to be spoken
        //   determine  Source = Real and SpeakerType = Human - this came off a
        //              live microphone, not out of a synthesiser, which is the
        //              same distinction MMC-TTS records in the other direction
        //              when it writes Synthetic and Agent
        //
        // What this REPLACES is AsSpeech(), whose comment reads "Audio == Speech
        // at this level" and which relabels the object and carries the audio
        // qualifier across. That records nothing about where the sound came from
        // or why a consumer should believe it contains speech - and if the two
        // really were equal at that level, OSD-AUO and OSD-SPO would not need to
        // be separate Data Types.
        //
        // Note this remains an ASSERTION, not a verification: nothing here
        // detects whether anyone actually spoke. That is why a room noise
        // recording still reaches MMC-ASR and comes back as
        // "(speaking in foreign language)". Voice activity detection would make
        // the claim checkable, and belongs in this AIM if it is wanted.
        var speech = BasicSpeechObject.FromData(
            audio.Data,
            new SpeechQualifier
            {
                SpeechQualifierID = System.Guid.NewGuid().ToString(),
                Attributes = new SpeechAttributes
                {
                    Source = SpeechSource.Real,
                    Metadata = new SpeechMetadata
                    {
                        Language = requestQualifier?.Attributes?.Metadata?.Language,
                        SpeakerProperties = new SpeakerProperties
                        {
                            SpeakerType  = SpeakerType.Human,
                            SpeakerCount = 1
                        }
                    }
                }
            });

        // Keep what was captured, and say where. When a transcription comes back
        // as a sentence the speaker never said, the only way to tell a deaf
        // microphone from a deaf model is to LISTEN to the recording - and by
        // then the bytes are inside a Message and gone. Written unconditionally
        // rather than behind a setting: it is a few seconds of audio, and the one
        // time it is wanted is the time nobody thought to switch it on.
        try
        {
            var captureFolder = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "mpai-captures");

            System.IO.Directory.CreateDirectory(captureFolder);

            var capturePath = System.IO.Path.Combine(
                captureFolder,
                $"captured-{System.DateTime.Now:yyyyMMdd-HHmmss}.wav");

            System.IO.File.WriteAllBytes(capturePath, audio.Data);
            System.Console.WriteLine($"[MMC-SOA-V2.5] saved {capturePath}");
        }
        catch (System.Exception failure)
        {
            System.Console.WriteLine($"[MMC-SOA-V2.5] could not save the capture: {failure.Message}");
        }
        var json   = MpaiJson.ToJson(speech);

        return new Message
        {
            MessageId   = message.MessageId,
            MessageType = "BasicSpeechObject",
            DataType    = "OSD-SPO-V1.5",
            Payload     = json,
            Ports       = new Dictionary<string, string> { [_outputPort] = json }
        };
    }
}
