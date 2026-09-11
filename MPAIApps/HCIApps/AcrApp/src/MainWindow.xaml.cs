using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

using AIF.Controller;   // AifError
using AIF.Store;        // AmdStore (provider factory)

using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Aims.Visual;   // WebcamVisualAcquisition, VisualAcquisitionRequest
using Mpai.UaKit;         // AvatarUaHost
using Mpai.Hci.Api;       // NorthApi, SpeakingAvatar

namespace AcrApp;

// ACR User Agent - drives MMC-ACR-V2.5 through the type-addressed North API.
// The UA does only real-world I/O (render avatar, capture camera+microphone,
// TYPE the name) and orchestration; it identifies data ONLY by data type:
//   Start -> type name -> "look at camera" -> supply OSD-BVO (face) + OSD-STM #1
//         -> flow suspends -> "speak a sentence" -> supply OSD-BSO (speech) +
//            OSD-STM #2 + OSD-BTO #1 (confirmation) + MMC-EPS + OSD-BTO #2 (name)
//         -> EFD/ESD write the enrolment to the shared gallery via the Controller
//         -> read OSD-BSO (spoken confirmation) + PAF-FDO (avatar) -> Stop.
// The name is TYPED (ASR is unreliable for bare names) and is the enrolment key.
public partial class MainWindow : Window
{
    private const string AcrModule = "MMC-ACR-V2.5";
    private const string RsrModule = "PAF-RSR-V1.6";

    // data types the UA speaks at the boundary
    private const string BVO = "OSD-BVO-V1.5";   // visual object (face)
    private const string BSO = "OSD-BSO-V1.5";   // speech object
    private const string BTO = "OSD-BTO-V1.5";   // text object (Response #1, UserName #2)
    private const string STM = "OSD-STM-V1.5";   // time (FaceTime #1, SpeechTime #2)
    private const string EPS = "MMC-EPS-V2.5";   // personal status

    private static readonly string AmdDir       = Mpai.Core.MpaiPaths.Amds;
    private static readonly string SettingsPath = Mpai.Core.MpaiPaths.Settings;
    private static readonly string AssetsDir    = Mpai.Core.MpaiPaths.Assets;

    private NorthApi?     _north;
    private AvatarUaHost? _avatar;
    private TaskCompletionSource<string>? _typedName;

    private static void Diag(string s)
    {
        try { System.IO.File.AppendAllText(@"C:\Users\Leonardo\Downloads\acr-diag.log",
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

            await Task.Run(() =>
                _north = new NorthApi(AmdDir, SettingsPath, store => new AcrProvider(store)));

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
        if (_north is null || _avatar is null) return;
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
        var started = await Task.Run(() => _north!.StartFlow(AcrModule));
        Diag("StartFlow " + AcrModule + " -> " + started);
        if (started != AifError.OK) { SetStatus("could not start " + AcrModule); return; }

        try
        {
            // 1) Name - typed (no ASR).
            InstructionText.Text = "Please type your name.";
            await RenderPromptAsync("Welcome to the CAV Access Control Registration Service. Please type your name.");
            var userName = (await PromptTypedNameAsync()).Trim();
            if (string.IsNullOrWhiteSpace(userName)) { SetStatus("no name given"); return; }
            Diag("name='" + userName + "'");

            // 2) Face - prompt, ~1s to turn, capture. Supply OSD-BVO + OSD-STM #1 + name (OSD-BTO #2).
            InstructionText.Text = "Look at the camera.";
            var speakLook = RenderPromptAsync("Look at the camera.");
            await Task.Delay(TimeSpan.FromSeconds(1));
            var face = await CaptureFaceAsync();
            await speakLook;

            var faceIn = new List<NorthApi.Datum>();
            if (face is not null) faceIn.Add(new NorthApi.Datum(BVO, MpaiJson.ToJson(face)));
            faceIn.Add(new NorthApi.Datum(STM, 1, MpaiJson.ToJson(NowSimpleTime())));
            faceIn.Add(new NorthApi.Datum(BTO, 2, MpaiJson.ToJson(BasicTextObject.FromText(userName))));
            Diag("face bytes=" + (face?.Data?.Length ?? 0) + " supplying BVO+STM#1+BTO#2(name)");
            var r1 = await Task.Run(() => _north!.Advance(AcrModule, faceIn));
            Diag("Advance(face) -> err=" + r1.Error + (r1.Suspended ? " suspended" : " completed"));
            if (!r1.Ok) { SetStatus("run error"); return; }

            var result = r1;

            // 3) Speech - on suspension, prompt, capture, and supply OSD-BSO + OSD-STM #2
            //    + confirmation (OSD-BTO #1) + Personal Status + name (OSD-BTO #2).
            if (r1.Suspended)
            {
                InstructionText.Text = "Please speak a short sentence so I can learn your voice.";
                await RenderPromptAsync("Please speak a short sentence so I can learn your voice.");
                var speech = await CaptureSpeechAsync();

                var thankYou = $"{userName}, thank you for joining the CAV Access Control Registration Service. You should speak your passphrase when you enter the service.";
                var speechIn = new List<NorthApi.Datum>();
                if (speech is not null) speechIn.Add(new NorthApi.Datum(BSO, MpaiJson.ToJson(speech)));
                speechIn.Add(new NorthApi.Datum(STM, 2, MpaiJson.ToJson(NowSimpleTime())));
                speechIn.Add(new NorthApi.Datum(BTO, 1, MpaiJson.ToJson(BasicTextObject.FromText(thankYou))));
                speechIn.Add(new NorthApi.Datum(EPS, MpaiJson.ToJson(LightSmileStatus())));
                speechIn.Add(new NorthApi.Datum(BTO, 2, MpaiJson.ToJson(BasicTextObject.FromText(userName))));
                Diag("speech bytes=" + (speech?.Data?.Length ?? 0) + " supplying BSO+STM#2+BTO#1(resp)+EPS+BTO#2(name)");
                var r2 = await Task.Run(() => _north!.Advance(AcrModule, speechIn));
                Diag("Advance(speech) -> err=" + r2.Error + (r2.Suspended ? " suspended" : " completed"));
                if (!r2.Ok) { SetStatus("resume error"); return; }
                result = r2;
            }

            // 4) EFD/ESD wrote the enrolment to Shared Storage via the Controller.
            //    Present the spoken confirmation (read by type).
            InstructionText.Text = "Registering...";
            byte[] wav = Array.Empty<byte>();
            var vj = result.ByType(BSO);
            if (!string.IsNullOrWhiteSpace(vj)) wav = MpaiJson.FromJson<BasicSpeechObject>(vj)?.Data ?? Array.Empty<byte>();
            FaceDescriptorsObject? avatarFace = null;
            var fj = result.ByType("PAF-FDO-V1.6");
            if (!string.IsNullOrWhiteSpace(fj)) avatarFace = MpaiJson.FromJson<FaceDescriptorsObject>(fj);

            Diag("outputs: vocalWav=" + wav.Length + " avatarFdo=" + (avatarFace==null?"nil":"present"));
            await _avatar!.PresentAsync(new SpeakingAvatar(wav, avatarFace));
            await Task.Delay(TimeSpan.FromSeconds(AvatarUaHost.WavDurationSeconds(wav) + 0.4));
            Diag("gallery entry expected under subject:" + userName);
            SetStatus($"registered: {userName}");
        }
        finally
        {
            await Task.Run(() => _north!.StopFlow(AcrModule));
        }
    }

    private async Task<BasicVisualObject?> CaptureFaceAsync()
    {
        try
        {
            var frame = await Task.Run(() =>
                new WebcamVisualAcquisition().AcquireAsync(new VisualAcquisitionRequest { VisualObjectType = "Face" })
                    .GetAwaiter().GetResult().Data);
            return (frame is { Length: > 0 }) ? BasicVisualObject.FromFile("probe.jpg", frame, "Face") : null;
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

    // --- Guidance prompts, via a one-shot PAF-RSR run (serious) ---
    private async Task RenderPromptAsync(string words)
    {
        if (_north is null) return;
        var inputs = new List<NorthApi.Datum>
        {
            new NorthApi.Datum(BTO, MpaiJson.ToJson(BasicTextObject.FromText(words))),
            new NorthApi.Datum(EPS, MpaiJson.ToJson(SeriousStatus()))
        };
        var r = await Task.Run(() => _north!.Advance(RsrModule, inputs));
        if (!r.Ok) return;
        byte[] wav = Array.Empty<byte>();
        var sj = r.ByType(BSO);
        if (!string.IsNullOrWhiteSpace(sj)) wav = MpaiJson.FromJson<BasicSpeechObject>(sj)?.Data ?? Array.Empty<byte>();
        FaceDescriptorsObject? fdo = null;
        var fj = r.ByType("PAF-FDO-V1.6");
        if (!string.IsNullOrWhiteSpace(fj)) fdo = MpaiJson.FromJson<FaceDescriptorsObject>(fj);
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

    private static EntityPersonalStatus LightSmileStatus() => new()
    {
        TextPersonalStatus = new TextPersonalStatus
        {
            TextEmotion = Emotion.Of(FactorLabel.Of("HAPPINESS", "light", null, 0.3))
        }
    };

    private void SetStatus(string s) => Dispatcher.Invoke(() => StatusText.Text = s);
}
