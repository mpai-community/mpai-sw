using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;

using AIF.Controller;   // AifError
using AIF.Store;        // AmdStore (provider factory)

using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Aims.Visual;   // WebcamVisualAcquisition, VisualAcquisitionRequest
using Mpai.UaKit;         // AvatarUaHost
using Mpai.Hci.Api;       // NorthApi, SpeakingAvatar

namespace MpdApp;

// UAD-MPD - the User Agent for Multimodal Personal Status-based Dialogue, driving
// MMC-MPD through the type-addressed North API. Each turn supplies the human's
// speech (OSD-BSO) + time (OSD-STM) + face (OSD-BVO); the Module perceives meaning
// (NLU) and feeling (ESI + EFI, multiplexed by PSM) and EDP replies with affect,
// spoken by the expressive avatar. Memory is EDP's running Summary; the Module
// lives from load to app close. No verb, no AIM, no port is named.
//
// Start UX: press Start -> spoken welcome, Start grays, models load; when loaded
// Start un-grays, the lady says the service is available, and the button becomes
// the Listen/Stop toggle (the rest is like MAD).
public partial class MainWindow : Window
{
    private const string MpdModule = "MMC-MPD-V2.5";
    private const string RsrModule = "PAF-RSR-V1.6";

    private const string BSO = "OSD-BSO-V1.5";   // speech object
    private const string STM = "OSD-STM-V1.5";   // acquisition time
    private const string BVO = "OSD-BVO-V1.5";   // face (visual object)
    private const string BTO = "OSD-BTO-V1.5";   // text object (spoken prompts)
    private const string FDO = "PAF-FDO-V1.6";   // face descriptors (avatar)

    private static readonly string AmdDir       = Mpai.Core.MpaiPaths.Amds;
    private static readonly string SettingsPath = Mpai.Core.MpaiPaths.Settings;
    private static readonly string AssetsDir    = Mpai.Core.MpaiPaths.Assets;

    private const string Welcome =
        "Welcome to the MPAI Multimodal Affective Dialogue Service, M M C - M P D. " +
        "The service uses a variety of artificial intelligence components that need to be loaded. " +
        "Please wait a few seconds for that to be completed.";
    private const string Available =
        "The M P D Service is available.";

    private NorthApi?     _north;
    private AvatarUaHost? _avatar;
    private bool _ready;       // NorthApi built (light)
    private bool _loaded;      // MMC-MPD started (models loaded)
    private int  _phase = 0;   // 0 = before Start, 1 = loaded / Listen toggle

    private static void Diag(string s)
    {
        try { System.IO.File.AppendAllText(@"C:\Users\Leonardo\Downloads\mpd-diag.log",
              DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + System.Environment.NewLine); } catch { }
    }

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            SetStatus("loading...");
            _avatar = new AvatarUaHost(Web, Dispatcher, AmdDir, AssetsDir);
            await _avatar.InitAsync();
            _avatar.RunningChanged += running => Dispatcher.Invoke(() =>
            {
                if (_phase == 1) ListenButton.Content = running ? "Stop" : "Listen";
            });

            // Build the Controller/UA (light) so RSR can speak the welcome before the
            // heavy MMC-MPD models are loaded on the first Start press.
            await Task.Run(() =>
                _north = new NorthApi(AmdDir, SettingsPath, store => new MpdProvider(store)));

            _ready = true;
            ListenButton.Content = "Start";
            ListenButton.IsEnabled = true;
            SetStatus("Press Start.");
        }
        catch (Exception fatal) { Program.Record("startup", fatal); SetStatus("startup failed: " + fatal.Message); }
    }

    private async void ListenButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _north is null || _avatar is null) return;

        if (_phase == 0)
        {
            // ---- Start press: welcome, gray, load models, then announce ready ----
            ListenButton.IsEnabled = false;                 // Start grays while loading
            SetStatus("welcome...");
            await RenderPromptAsync(Welcome);               // audio unlocked by this click
            SetStatus("loading models...");
            var started = await Task.Run(() => _north!.StartFlow(MpdModule));
            if (started != AifError.OK) { SetStatus("could not start " + MpdModule); ListenButton.IsEnabled = true; return; }
            _loaded = true;
            await RenderPromptAsync(Available);             // "The MPD Service is available."
            _phase = 1;
            // Auto-listen: no Listen press required. Button stays grayed until the
            // user actually speaks, at which point it becomes an enabled Stop.
            ListenButton.Content = "Stop";
            ListenButton.IsEnabled = false;
            SetStatus("Listening - just speak.");
            _avatar.StartLoop(HandleTurn);
            return;
        }

        // ---- phase 1: the button is Stop (revealed once the user has spoken) ----
        if (_avatar.IsRunning) { _avatar.StopLoop(); SetStatus("Stopped."); ListenButton.IsEnabled = false; }
    }

    // One dialogue turn, by data type: supply speech (+ time) + face; read the
    // machine's spoken reply (OSD-BSO) + avatar face (PAF-FDO). NLU/ESI/EFI/PSM
    // produce the Personal Status inside the Module; EDP replies with affect.
    private SpeakingAvatar HandleTurn(BasicSpeechObject speech)
    {
        try
        {
            if (Spoke(speech))
                Dispatcher.Invoke(() => { ListenButton.IsEnabled = true; });   // reveal Stop once the user speaks

            var inputs = new List<NorthApi.Datum>
            {
                new NorthApi.Datum(BSO, MpaiJson.ToJson(speech)),
                new NorthApi.Datum(STM, MpaiJson.ToJson(NowSimpleTime()))
            };
            var face = CaptureFace();
            if (face is not null) inputs.Add(new NorthApi.Datum(BVO, MpaiJson.ToJson(face)));

            var r = _north!.Advance(MpdModule, inputs);
            if (!r.Ok) { Diag("turn err=" + r.Error); return new SpeakingAvatar(Array.Empty<byte>(), null); }

            byte[] wav = Array.Empty<byte>();
            var sj = r.ByType(BSO);
            if (!string.IsNullOrWhiteSpace(sj)) wav = MpaiJson.FromJson<BasicSpeechObject>(sj)?.Data ?? Array.Empty<byte>();
            FaceDescriptorsObject? fdo = null;
            var fj = r.ByType(FDO);
            if (!string.IsNullOrWhiteSpace(fj)) fdo = MpaiJson.FromJson<FaceDescriptorsObject>(fj);

            Diag("turn: speechBytes=" + (speech.Data?.Length ?? 0) + " face=" + (face is null ? "nil" : "yes") + " replyWav=" + wav.Length);
            return new SpeakingAvatar(wav, fdo);
        }
        catch (Exception ex) { Diag("turn ex: " + ex.Message); return new SpeakingAvatar(Array.Empty<byte>(), null); }
    }

    // One-shot neutral spoken prompt via PAF-RSR (no PersonalStatus supplied).
    private async Task RenderPromptAsync(string words)
    {
        if (_north is null) return;
        var inputs = new List<NorthApi.Datum> { new NorthApi.Datum(BTO, MpaiJson.ToJson(BasicTextObject.FromText(words))) };
        var r = await Task.Run(() => _north!.Advance(RsrModule, inputs));
        if (!r.Ok) return;
        byte[] wav = Array.Empty<byte>();
        var sj = r.ByType(BSO);
        if (!string.IsNullOrWhiteSpace(sj)) wav = MpaiJson.FromJson<BasicSpeechObject>(sj)?.Data ?? Array.Empty<byte>();
        FaceDescriptorsObject? fdo = null;
        var fj = r.ByType(FDO);
        if (!string.IsNullOrWhiteSpace(fj)) fdo = MpaiJson.FromJson<FaceDescriptorsObject>(fj);
        await _avatar!.PresentAsync(new SpeakingAvatar(wav, fdo));
        await Task.Delay(TimeSpan.FromSeconds(AvatarUaHost.WavDurationSeconds(wav) + 0.3));
    }

    // Did the user actually speak (vs ambient noise)? Peak/RMS over the PCM16
    // samples; used ONLY to decide when to reveal the Stop button. It does not
    // gate the dialogue - every captured turn is still processed.
    private static bool Spoke(BasicSpeechObject speech)
    {
        var d = speech.Data;
        if (d is null || d.Length < 128) return false;
        int start = (d.Length > 12 && d[0] == 0x52 && d[1] == 0x49 && d[2] == 0x46 && d[3] == 0x46) ? 44 : 0;
        int peak = 0; long sumsq = 0; long n = 0;
        for (int i = start; i + 1 < d.Length; i += 2)
        {
            short v = (short)(d[i] | (d[i + 1] << 8));
            int a = v < 0 ? -v : v;
            if (a > peak) peak = a;
            sumsq += (long)v * v; n++;
        }
        double rms = n > 0 ? System.Math.Sqrt(sumsq / (double)n) : 0;
        return peak >= 2000 && rms >= 400;
    }

    private BasicVisualObject? CaptureFace()
    {
        try
        {
            var frame = new WebcamVisualAcquisition()
                .AcquireAsync(new VisualAcquisitionRequest { VisualObjectType = "Face" })
                .GetAwaiter().GetResult().Data;
            return (frame is { Length: > 0 }) ? BasicVisualObject.FromFile("webcam.jpg", frame, "Face") : null;
        }
        catch { return null; }
    }

    private static SimpleTime NowSimpleTime()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        return new SimpleTime
        {
            SimpleTimeID = Guid.NewGuid().ToString("N"),
            SimpleTimeData = new List<TimeSegment>
            { new TimeSegment { FlagsByte = 0, StartTime = now, EndTime = now, AccuracyMode = "single", AccuracyPlusMinus = 0.0, TimeType = true } }
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        try { if (_loaded) { _north?.StopFlow(MpdModule); _loaded = false; } } catch { }
        base.OnClosed(e);
    }

    private void SetStatus(string s) => Dispatcher.Invoke(() => StatusText.Text = s);
}