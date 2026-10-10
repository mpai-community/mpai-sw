using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.PhysicalLayer;

// THE PHYSICAL LAYER API (MPAI-AIF V3.0, User Agent 4): what a User Agent's Workflow
// interpreter calls to obtain data from the real world and to deliver data to it.
// Each Unit gives or takes an Object of a Data Type with its Qualifier. The Units
// acquire what a Qualifier asks for; which device serves it is the Unit's own matter.
// Where what is asked cannot be had, a Unit returns an Object with no data and a
// Qualifier saying what is available: nothing is substituted silently.
//
// These are the devices of THIS machine (Windows: WASAPI and WinMM through NAudio, the
// Windows camera, a WebView2 page for the avatar). A Unit never addresses an AIM and
// never reaches a Controller: what it acquires the User Agent gives to a Module's
// boundary Ports through the Controller API.

// MICROPHONE: a Basic Audio Object of a stated length, or - asserting that it is
// speech - a Basic Speech Object that ends when the speaker stops.
public sealed class MicrophoneUnit
{
    // Speech for recognition is 16 kHz mono 16-bit; other audio as the caller asks.
    public int SamplingFrequency { get; init; } = 16000;

    // A device takes a few hundred milliseconds to start, and a person answering
    // promptly speaks into that gap ("Yes, please" reached the recogniser as
    // "Please"): the wait is before the recording, so nothing spoken is lost.
    public TimeSpan Settle { get; init; } = TimeSpan.FromMilliseconds(400);

    // Audio of a stated length.
    public async Task<BasicAudioObject> AcquireAudioAsync(TimeSpan duration)
    {
        var mic = new WasapiAudioAcquisition(SamplingFrequency);
        return await mic.AcquireAsync(new AcquisitionRequest { Duration = duration });
    }

    // SPEECH, ending when the speaker stops (voice activity, by level): it waits for
    // speech to start - giving up after StartTimeout if nobody speaks - and stops when
    // the level stays low for Hangover; RunawayGuard caps the whole capture. The
    // claim that it is speech is made in the Qualifier: Source Real, Speaker Human,
    // and the Language the caller expects (only the caller knows what is about to be
    // spoken; recognition reads it from there). One capture, one fresh device: a
    // lingering one keeps buffered audio that would start the next capture at once.
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan Hangover { get; init; } = TimeSpan.FromMilliseconds(1900);
    public TimeSpan RunawayGuard { get; init; } = TimeSpan.FromSeconds(30);
    public double SpeakLevel { get; init; } = 0.02;
    public double SilenceLevel { get; init; } = 0.015;

    public async Task<BasicSpeechObject> AcquireSpeechAsync(Language? language = null, CancellationToken cancel = default)
    {
        var mic = new WasapiAudioAcquisition(SamplingFrequency);
        await Task.Delay(Settle, cancel);
        mic.StartAcquire();
        var start = DateTime.UtcNow;
        var started = false;
        DateTime? quietSince = null;
        while (!cancel.IsCancellationRequested)
        {
            await Task.Delay(25, CancellationToken.None);
            var level = mic.CurrentLevel;
            var now = DateTime.UtcNow;
            if (now - start > RunawayGuard) break;
            if (!started)
            {
                if (level > SpeakLevel) started = true;
                else if (now - start > StartTimeout) break;
            }
            else if (level < SilenceLevel) { quietSince ??= now; if (now - quietSince.Value >= Hangover) break; }
            else quietSince = null;
        }
        var audio = await mic.StopAcquireAsync();
        if (!started) audio = new BasicAudioObject { AudioQualifier = audio.AudioQualifier };   // nobody spoke: no data
        return BasicSpeechObject.FromData(audio.Data, new SpeechQualifier
        {
            SpeechQualifierID = Guid.NewGuid().ToString(),
            // What the bytes are, from the device that knew (raw PCM, no header).
            Format = new SpeechFormat
            {
                ContentFormats = new SpeechContentFormats { RawData = audio.AudioQualifier?.Formats?.ContentFormat?.RawData?.SampleSpace }
            },
            Attributes = new SpeechAttributes
            {
                Source = SpeechSource.Real,
                Metadata = new SpeechMetadata
                {
                    Language = language,
                    SpeakerProperties = new SpeakerProperties { SpeakerType = SpeakerType.Human, SpeakerCount = 1 }
                }
            }
        });
    }
}

// CAMERA: a Basic Visual Object - a still of the person in front of the device.
public sealed class CameraUnit
{
    public int CameraIndex { get; init; }

    // A face, or null when the camera gave nothing.
    public async Task<BasicVisualObject?> AcquireFaceAsync()
    {
        var frame = await new WebcamVisualAcquisition(CameraIndex)
            .AcquireAsync(new VisualAcquisitionRequest { VisualObjectType = "Face" });
        return frame.Data is { Length: > 0 } data ? BasicVisualObject.FromFile("webcam.jpg", data, "Face") : null;
    }
}

// LOUDSPEAKER: plays a Basic Speech Object.
public sealed class LoudspeakerUnit
{
    public Task DeliverAsync(BasicSpeechObject speech) => new WinmmSpeechDelivery().DeliverAsync(speech);

    // AN AUDIO OBJECT OR AN AUDIO SCENE, as the User hears it (the author, 2026/10/04:
    // "AOD is UA's business"): rendered from the User Point of View by the spatial
    // renderer (Mpai.SpatialAudio, Steam Audio in steamAudioFolder), then played.
    // What was played is returned.
    public async Task<BasicAudioObject> DeliverAsync(string objectOrScene, string steamAudioFolder,
                                                     Mpai.SpatialAudio.SpatialRenderer.Layout layout = Mpai.SpatialAudio.SpatialRenderer.Layout.Binaural)
    {
        var heard = new Mpai.SpatialAudio.AudioRendering(steamAudioFolder, layout).Render(objectOrScene);
        await PlayAsync(heard.Data);
        return heard;
    }

    // A WAV, played to the end.
    public static async Task PlayAsync(byte[] wav)
    {
        if (wav.Length == 0) return;
        using var reader = new NAudio.Wave.WaveFileReader(new MemoryStream(wav));
        using var output = new NAudio.Wave.WaveOutEvent();
        output.Init(reader);
        output.Play();
        while (output.PlaybackState == NAudio.Wave.PlaybackState.Playing)
            await Task.Delay(100);
    }
}

// AVATAR: the 3D page that draws and animates the speaking avatar (cav-webview.html,
// in a WebView2 the application owns): it plays a Speaking Avatar unpacked - the
// speech with the Face and Body Descriptors - and draws a 3D Model Scene from a Point
// of View. post sends one message to the page.
public sealed class AvatarUnit(Func<string, Task> post)
{
    private readonly WebView3DModelDelivery page = new(post);

    public Task DeliverAsync(byte[] speechWav, FaceDescriptorsObject? face, BodyDescriptorsObject? body) =>
        page.DeliverWithSpeechAsync(Basic3DModelObject.FromData([]), face, speechWav, body);
}
