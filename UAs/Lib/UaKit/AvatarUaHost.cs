using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;

using Microsoft.Web.WebView2.Wpf;
using Microsoft.Web.WebView2.Core;

using AIF.Store;
using AIF.Controller;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Hci.Api;    // SpeakingAvatar
using Mpai.Osd.Tod;    // WebView3DModelDelivery
using Mpai.Aims.Audio; // WasapiAudioAcquisition (the mic device - real-world edge)
using Mpai.Aims.Speech;// SoaAimProcessor (Speech Object Acquisition - real-world edge)

namespace Mpai.UaKit;

// AvatarUaHost - the reusable plumbing shared by User Agent applications that
// present a speaking avatar. It owns the real-world edges every such UA needs:
//   - the OUTPUT edge: a WebView-hosted 3D avatar renderer (3OD's device), to which
//     it delivers a Speaking Avatar (Machine Speech + Face Descriptors) for playback
//     and lip-sync;
//   - the INPUT edge: microphone capture via Speech Object Acquisition with
//     voice-activity auto-stop, driving a continuous listen loop.
// The application supplies only two things: the WebView2 control to render into, and
// a per-turn handler that takes the captured Speech Object and returns the Speaking
// Avatar to present (typically a single call to the HCI API - Converse, Translate, ...).
// This removes the duplicated WebView/capture/present code from each UA app.
public sealed class AvatarUaHost
{
    private const string SoaModule = "MMC-SOA-V2.5";

    private readonly WebView2   _web;
    private readonly Dispatcher _ui;
    private readonly string     _amdDir;
    private readonly string     _assetsHost;   // virtual host name (e.g. "cavapp.local")
    private readonly string     _assetsDir;    // folder mapped to the virtual host (viewer + avatar)

    private WebView3DModelDelivery? _renderer;
    private volatile bool           _running;

    // Raised when the listen loop starts or stops (true = running), on the UI thread,
    // so the app can update its Listen/Stop button.
    public event Action<bool>? RunningChanged;

    public bool IsRunning => _running;

    public AvatarUaHost(
        WebView2   web,
        Dispatcher ui,
        string     amdDir,
        string     assetsDir,
        string     assetsHost = "cavapp.local")
    {
        _web        = web;
        _ui         = ui;
        _amdDir     = amdDir;
        _assetsDir  = assetsDir;
        _assetsHost = assetsHost;
    }

    // Initialise the WebView2 renderer: allow audio autoplay (the speech plays from a
    // message handler, not an in-page click), map the assets virtual host, and load
    // the avatar viewer. Call once, after the window is loaded.
    public async Task InitAsync(string viewerPage = "cav-webview.html")
    {
        // A DEDICATED user-data folder per application. WebView2 reuses an existing
        // environment when the user-data folder matches, and the FIRST environment's
        // browser arguments win - so a shared (default) folder makes the autoplay
        // flag below be ignored, and the avatar's speech is silently blocked (the
        // face still animates - only audio is autoplay-gated). A per-app folder makes
        // the --autoplay-policy flag actually take effect, so the avatar can speak
        // without a preceding user gesture.
        var appName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "UaKit";
        var userDataFolder = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "MpaiUaKit", appName);
        System.IO.Directory.CreateDirectory(userDataFolder);

        var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder,
            new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));
        await _web.EnsureCoreWebView2Async(env);
        _web.CoreWebView2.Settings.IsWebMessageEnabled = true;
        _web.CoreWebView2.IsMuted = false;   // never mute the avatar's voice
        _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
            _assetsHost, _assetsDir, CoreWebView2HostResourceAccessKind.Allow);
        _web.CoreWebView2.Navigate($"https://{_assetsHost}/{viewerPage}");

        _renderer = new WebView3DModelDelivery(json =>
            _ui.InvokeAsync(() => _web.CoreWebView2.PostWebMessageAsJson(json)).Task);
    }

    // Present one Speaking Avatar on the renderer (used for a single turn, e.g. typed input).
    public async Task PresentAsync(SpeakingAvatar avatar)
    {
        if (_renderer is null) return;
        await _ui.InvokeAsync(async () =>
        {
            var model = Basic3DModelObject.FromData(Array.Empty<byte>());
            await _renderer.DeliverWithSpeechAsync(model, avatar.FaceDescriptors, avatar.MachineSpeechWav);
        });
    }

    // Start the continuous listen loop: capture a spoken turn (VAD auto-stop) -> hand it
    // to the app's handler -> present the returned Speaking Avatar -> wait for the avatar
    // to finish speaking -> listen again, until StopLoop. Empty captures (nobody spoke)
    // simply listen again. The handler runs off the UI thread.
    public void StartLoop(Func<BasicSpeechObject, SpeakingAvatar> handleTurn)
    {
        if (_running) return;
        _running = true;
        RunningChanged?.Invoke(true);
        _ = Task.Run(() => LoopAsync(handleTurn));
    }

    public void StopLoop()
    {
        if (!_running) return;
        _running = false;
        RunningChanged?.Invoke(false);
    }

    private async Task LoopAsync(Func<BasicSpeechObject, SpeakingAvatar> handleTurn)
    {
        try
        {
            while (_running)
            {
                var speech = CaptureSpeech();
                if (!_running) break;
                if (speech is null || speech.Data.Length == 0) continue;

                var avatar = await Task.Run(() => handleTurn(speech));
                await PresentAsync(avatar);

                // Let the avatar finish speaking before listening again, so the mic
                // does not capture its own voice.
                var speakSeconds = WavDurationSeconds(avatar.MachineSpeechWav);
                await Task.Delay(TimeSpan.FromSeconds(speakSeconds + 0.6));
            }
        }
        catch (Exception ex)
        {
            await _ui.InvokeAsync(() => System.Windows.MessageBox.Show(ex.ToString(), "UA error"));
        }
        finally { StopLoop(); }
    }

    // Capture one spoken turn from the microphone as a Speech Object, with voice-
    // activity auto-stop (Speech Object Acquisition is the UA's real-world input edge).
    // The recognition happens inside the Module, not here.
    public BasicSpeechObject? CaptureSpeech()
    {
        var store = new AmdStore(_amdDir);
        store.Scan();
        // Dispose the WASAPI device after each capture. Back-to-back captures (e.g.
        // a greeting "yes" then a passphrase) must each start from a CLEAN mic - a
        // lingering device keeps buffered audio (the tail of the previous utterance
        // or the prompt's echo) that would trip the voice-activity START immediately
        // and end the next capture on a fragment. One capture, one fresh device.
        var mic = new WasapiAudioAcquisition();
        var soa = new SoaAimProcessor(SoaModule, mic, AimPortReader.Load(store, SoaModule), vadAutoStop: true);
        var msg = new Message
        {
            MessageId = Guid.NewGuid().ToString(),
            Ports = new Dictionary<string, string>()
        };
        var outcome = soa.ProcessAsync(msg).GetAwaiter().GetResult();
        var speechJson = outcome.Ports.Values.FirstOrDefault() ?? "";
        return string.IsNullOrWhiteSpace(speechJson) ? null : MpaiJson.FromJson<BasicSpeechObject>(speechJson);
    }

    // Duration of a 16-bit PCM WAV, in seconds, from its bytes.
    public static double WavDurationSeconds(byte[] wav)
    {
        try
        {
            if (wav.Length < 44) return 1.0;
            int channels   = BitConverter.ToInt16(wav, 22);
            int sampleRate = BitConverter.ToInt32(wav, 24);
            int bits       = BitConverter.ToInt16(wav, 34);
            if (channels <= 0 || sampleRate <= 0 || bits <= 0) return 1.0;
            int pos = 12, dataLen = wav.Length - 44;
            while (pos + 8 <= wav.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
                int len = BitConverter.ToInt32(wav, pos + 4);
                if (id == "data") { dataLen = Math.Min(len, wav.Length - (pos + 8)); break; }
                pos += 8 + len + (len & 1);
            }
            double bytesPerSec = sampleRate * channels * (bits / 8.0);
            return bytesPerSec > 0 ? dataLen / bytesPerSec : 1.0;
        }
        catch { return 1.0; }
    }
}
