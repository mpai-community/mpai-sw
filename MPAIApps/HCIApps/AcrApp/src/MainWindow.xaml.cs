using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

using AIF.Controller;
using AIF.Store;

using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Aims.Visual;   // WebcamVisualAcquisition, VisualAcquisitionRequest
using Mpai.UaKit;         // AvatarUaHost
using Mpai.Hci.Api;       // SpeakingAvatar

namespace AcrApp;

// ACR User Agent. It DRIVES the MMC-ACR-V2.5 Module through the Controller,
// exactly as UAs\Orchestration\HCI-ACR.orch prescribes. The UA does only
// real-world I/O (render avatar, capture camera+microphone, TYPE the name),
// timing, and persistence to Shared Storage; it never runs an AIM.
//   Start -> type name -> "look at camera" (+1s) -> supply FaceObject/FaceTime
//         -> "speak a sentence" -> supply SpeechObject/SpeechTime + the fixed
//            confirmation Response + a light-smile Personal Status
//         -> the Controller runs EFD, ESD and RSR (by data type per the L3)
//         -> UA reads the Face/Speech Descriptors (with their stamped times)
//         -> UA writes {name -> descriptors, times} to Shared Storage (the same
//            gallery MMC-MAC reads) -> present the confirmation -> Stop.
// The name is TYPED (ASR is unreliable for bare names).
public partial class MainWindow : Window
{
    private const string AcrModule = "MMC-ACR-V2.5";
    private const string RsrModule = "PAF-RSR-V1.6";        // renders the guidance prompts
    private const string GalleryScope = "MMC-MAC-V2.5";     // SAME Shared-Storage scope MMC-MAC reads

    private static readonly string AmdDir      = Mpai.Core.MpaiPaths.Amds;
    private static readonly string SettingsPath= Mpai.Core.MpaiPaths.Settings;
    private static readonly string AssetsDir   = Mpai.Core.MpaiPaths.Assets;

    private UserAgent?    _ua;
    private AcrProvider?  _provider;
    private AimSettings?  _settings;
    private AvatarUaHost? _avatar;
    private readonly object _uaLock = new();

    private TaskCompletionSource<string>? _typedName;
    private int _acrId = -1;

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

            await Task.Run(() =>
            {
                var store = new AmdStore(AmdDir); store.Scan();
                _settings = AimSettings.Load(SettingsPath);
                _provider = new AcrProvider(store);
                _ua       = new UserAgent(store);
                _ua.MPAI_AIFU_Controller_Initialize();
            });

            SetStatus("Ready. Press Register to begin.");
            InstructionText.Text = "Press Register to begin.";
            StartButton.IsEnabled = true;
        }
        catch (Exception fatal)
        {
            Program.Record("startup", fatal);
            SetStatus($"startup failed: {fatal.Message}");
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_ua is null || _avatar is null) return;
        StartButton.IsEnabled = false;
        try { await RunFlowAsync(); }
        catch (Exception ex) { Program.Record("flow", ex); SetStatus("error: " + ex.Message); }
        finally
        {
            InstructionText.Text = "Press Register to enrol another person.";
            StartButton.IsEnabled = true;
        }
    }

    private async Task RunFlowAsync()
    {
        var started = await Task.Run(() => _ua!.MPAI_AIFU_MODULE_Start(AcrModule, _provider!, _settings!, out _acrId));
        if (started != AifError.OK) { SetStatus("could not start MMC-ACR-V2.5"); return; }

        try
        {
            // 1) Name - typed characters (no ASR).
            InstructionText.Text = "Please type your name.";
            await RenderPromptAsync("Welcome to the CAV Access Control Registration Service. Please type your name.");
            var userName = (await PromptTypedNameAsync()).Trim();
            if (string.IsNullOrWhiteSpace(userName)) { SetStatus("no name given"); return; }

            // 2) Face - prompt, ~1s to turn, capture; time from the UA clock.
            InstructionText.Text = "Look at the camera.";
            var speakLook = RenderPromptAsync("Look at the camera.");
            await Task.Delay(TimeSpan.FromSeconds(1));
            var face = await CaptureFaceAsync();
            await speakLook;

            var faceBoundary = new Dictionary<string, string>();
            if (face is not null) faceBoundary["FaceObject"] = MpaiJson.ToJson(face);
            faceBoundary["FaceTime"] = MpaiJson.ToJson(NowSimpleTime());
            faceBoundary["UserName"] = MpaiJson.ToJson(BasicTextObject.FromText(userName));

            var (e1, out1) = await Task.Run(() => _ua!.RunAsync(_acrId, faceBoundary).GetAwaiter().GetResult());
            if (e1 != AifError.OK || out1 is null) { SetStatus("run error"); return; }

            // 3) Speech - prompt, capture; supply speech+time + the confirmation
            //    Response and a light-smile Personal Status for the RSR utterance.
            var outcome = out1;
            if (outcome.Suspended)
            {
                InstructionText.Text = "Please speak a short sentence so I can learn your voice.";
                await RenderPromptAsync("Please speak a short sentence so I can learn your voice.");
                var speech = await CaptureSpeechAsync();

                var thankYou = $"{userName}, thank you for joining the CAV Access Control Registration Service. You should speak your passphrase when you enter the service.";
                var resume = new Dictionary<string, string>();
                if (speech is not null) resume["SpeechObject"] = MpaiJson.ToJson(speech);
                resume["SpeechTime"]     = MpaiJson.ToJson(NowSimpleTime());
                resume["Response"]       = MpaiJson.ToJson(BasicTextObject.FromText(thankYou));
                resume["PersonalStatus"] = MpaiJson.ToJson(LightSmileStatus());
                resume["UserName"]       = MpaiJson.ToJson(BasicTextObject.FromText(userName));

                var (e2, out2) = await Task.Run(() => _ua!.ResumeAsync(_acrId, resume).GetAwaiter().GetResult());
                if (e2 != AifError.OK || out2 is null) { SetStatus("resume error"); return; }
                outcome = out2;
            }

            var completed = outcome.Completed;
            if (completed is null) { SetStatus("the Module did not complete"); return; }

            // The Module (EFD/ESD) wrote the enrolment to Shared Storage itself,
            // via the Controller Shared-Storage API. The UA does not touch the gallery.
            InstructionText.Text = "Registering...";

            // 6) Present the confirmation (the Module's RSR spoke it, light smile).
            byte[] wav = Array.Empty<byte>(); FaceDescriptorsObject? avatarFace = null;
            if (completed.Ports.TryGetValue("VocalResponse", out var vj) && !string.IsNullOrWhiteSpace(vj))
                wav = MpaiJson.FromJson<BasicSpeechObject>(vj)?.Data ?? Array.Empty<byte>();
            if (completed.Ports.TryGetValue("MachineFaceDescriptors", out var mfj) && !string.IsNullOrWhiteSpace(mfj))
                avatarFace = MpaiJson.FromJson<FaceDescriptorsObject>(mfj);

            await _avatar!.PresentAsync(new SpeakingAvatar(wav, avatarFace));
            await Task.Delay(TimeSpan.FromSeconds(AvatarUaHost.WavDurationSeconds(wav) + 0.4));
            SetStatus($"registered: {userName}");
        }
        finally
        {
            var id = _acrId; _acrId = -1;
            await Task.Run(() => _ua!.MPAI_AIFU_MODULE_Stop(id));
        }
    }

    private async Task<BasicVisualObject?> CaptureFaceAsync()
    {
        try
        {
            var frame = await Task.Run(() =>
                new WebcamVisualAcquisition().AcquireAsync(new VisualAcquisitionRequest { VisualObjectType = "Face" })
                    .GetAwaiter().GetResult().Data);
            return (frame is { Length: > 0 }) ? BasicVisualObject.FromFile("probe.jpg", frame) : null;
        }
        catch { return null; }
    }

    private async Task<BasicSpeechObject?> CaptureSpeechAsync()
    {
        try
        {
            var wav = await Task.Run(() => _avatar!.CaptureSpeech()?.Data);
            return (wav is { Length: > 0 }) ? BasicSpeechObject.FromData(wav, null) : null;
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
            {
                new TimeSegment { FlagsByte = 0, StartTime = now, EndTime = now, AccuracyMode = "single", AccuracyPlusMinus = 0.0, TimeType = true }
            }
        };
    }

    // --- Typed-name box (Enter or Confirm) ---
    private Task<string> PromptTypedNameAsync()
    {
        _typedName = new TaskCompletionSource<string>();
        Dispatcher.Invoke(() => { NamePanel.Visibility = Visibility.Visible; NameBox.Text = ""; NameBox.Focus(); });
        return _typedName.Task;
    }
    private void CompleteTypedName()
    {
        var tcs = _typedName; _typedName = null;
        if (tcs is null) return;
        Dispatcher.Invoke(() => NamePanel.Visibility = Visibility.Collapsed);
        tcs.TrySetResult(NameBox.Text ?? "");
    }
    private void ConfirmName_Click(object sender, RoutedEventArgs e) => CompleteTypedName();
    private void NameBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; CompleteTypedName(); } }

    // --- Guidance prompts, rendered by a separate PAF-RSR run (serious) ---
    private async Task RenderPromptAsync(string words)
    {
        var boundary = new Dictionary<string, string>
        {
            ["TextObject"]     = MpaiJson.ToJson(BasicTextObject.FromText(words)),
            ["PersonalStatus"] = MpaiJson.ToJson(SeriousStatus())
        };
        var done = await Task.Run(() => RunRsr(boundary));
        if (done is null) return;
        byte[] wav = Array.Empty<byte>(); FaceDescriptorsObject? fdo = null;
        if (done.Ports.TryGetValue("MachineSpeech", out var sj) && !string.IsNullOrWhiteSpace(sj))
            wav = MpaiJson.FromJson<BasicSpeechObject>(sj)?.Data ?? Array.Empty<byte>();
        if (done.Ports.TryGetValue("MachineFaceDescriptors", out var fj) && !string.IsNullOrWhiteSpace(fj))
            fdo = MpaiJson.FromJson<FaceDescriptorsObject>(fj);
        await _avatar!.PresentAsync(new SpeakingAvatar(wav, fdo));
        await Task.Delay(TimeSpan.FromSeconds(AvatarUaHost.WavDurationSeconds(wav) + 0.3));
    }

    private static EntityPersonalStatus SeriousStatus() => new()
    {
        TextPersonalStatus = new TextPersonalStatus
        {
            TextEmotion        = Emotion.Of(FactorLabel.Of("CALMNESS", "serious", null, 0.7)),
            TextSocialAttitude = SocialAttitude.Of(FactorLabel.Of("SOCIAL RANK", "serious", null, 0.7))
        }
    };

    // A light smile for the closing confirmation - low-intensity HAPPINESS, so
    // GFD renders a gentle smile (demonstrating EPS driving the face).
    private static EntityPersonalStatus LightSmileStatus() => new()
    {
        TextPersonalStatus = new TextPersonalStatus
        {
            TextEmotion = Emotion.Of(FactorLabel.Of("HAPPINESS", "light", null, 0.3))
        }
    };

    private AIF.Controller.Message? RunRsr(Dictionary<string, string> boundary)
    {
        if (_ua is null) return null;
        lock (_uaLock)
        {
            if (_ua.MPAI_AIFU_MODULE_Start(RsrModule, _provider!, _settings!, out var rid) != AifError.OK) return null;
            try
            {
                var (error, outcome) = _ua.RunAsync(rid, boundary).GetAwaiter().GetResult();
                if (error != AifError.OK || outcome?.Completed is null || outcome.Completed.IsError) return null;
                return outcome.Completed;
            }
            finally { _ua.MPAI_AIFU_MODULE_Stop(rid); }
        }
    }

    private void SetStatus(string s) => Dispatcher.Invoke(() => StatusText.Text = s);
}
