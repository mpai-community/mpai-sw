using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

using AIF.Controller;
using AIF.Store;

using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Aims.Visual;   // WebcamVisualAcquisition, VisualAcquisitionRequest
using Mpai.UaKit;         // AvatarUaHost
using Mpai.Hci.Api;       // SpeakingAvatar

namespace HciMac;

// HCI-MAC User Agent.  Realises UAs/Orchestration/HCI-MAC.orch step-for-step
// (see M3167 - HCI-MAC step-by-step operation). The UA has two roles only:
//   * real-world I/O limbs: render the avatar, capture the webcam frame, capture
//     the microphone - and it stamps the acquired Face Object's qualifier
//     (VisualObjectType = Face) at acquisition, because MAC has no CVE-VSI stage;
//   * orchestration: it tells the Controller what to do, in the order the
//     guidebook (.orch) prescribes, and reads back the boundary outputs.
// It drives the MMC-MAC-V2.5 Module through the Controller ONLY:
//   Start -> write boundary inputs (FaceObject/FaceTime, then, on the Module's
//   request, SpeechObject/SpeechTime) -> read boundary outputs
//   (UserID, VocalResponse, FaceDescriptors) -> Stop.
// It never names a sub-AIM, never orders their execution, never wires them.
public partial class MainWindow : Window
{
    private const string MacModule = "MMC-MAC-V2.5";
    private const string RsrModule = "PAF-RSR-V1.6";   // renders the fixed spoken prompts

    private static readonly string AmdDir       = Mpai.Core.MpaiPaths.Amds;
    private static readonly string SettingsPath = Mpai.Core.MpaiPaths.Settings;
    private static readonly string AssetsDir    = Mpai.Core.MpaiPaths.Assets;
    private static readonly string GalleryJson  = Mpai.Core.MpaiPaths.Gallery;

    private UserAgent?    _ua;
    private MacProvider?  _provider;
    private AimSettings?  _settings;
    private AvatarUaHost? _avatar;
    private int _macId = -1;

    // ---- Diagnostics: FOLLOW THE CONTENT. Toggle with DiagOn. -------------
    private static bool DiagOn = true;   // set false to silence
    private static void Diag(string s)
    {
        if (!DiagOn) return;
        try { System.IO.File.AppendAllText(@"C:\Users\Leonardo\Downloads\mac-diag.log",
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
            {
                var store = new AmdStore(AmdDir); store.Scan();
                _settings = AimSettings.Load(SettingsPath);
                _provider = new MacProvider(store, GalleryJson);
                _ua       = new UserAgent(store);
                _ua.MPAI_AIFU_Controller_Initialize();
            });

            InstructionText.Text = "Press Start to begin.";
            SetStatus("Ready.");
            StartButton.IsEnabled = true;
        }
        catch (Exception fatal)
        {
            Program.Record("startup", fatal);
            SetStatus("startup failed: " + fatal.Message);
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_ua is null || _avatar is null) return;
        StartButton.IsEnabled = false;
        HideResult();
        try { await RunFlowAsync(); }
        catch (Exception ex) { Program.Record("flow", ex); SetStatus("error: " + ex.Message); }
        finally
        {
            InstructionText.Text = "Press Start to authenticate again.";
            StartButton.IsEnabled = true;
        }
    }

    // The M3167 sequence, realised as UA -> Controller calls.
    private async Task RunFlowAsync()
    {
        // Start the Module (Controller builds MMC-MAC-V2.5 from its L3).
        var started = await Task.Run(() =>
            _ua!.MPAI_AIFU_MODULE_Start(MacModule, _provider!, _settings!, out _macId));
        Diag("MODULE_Start " + MacModule + " -> err=" + started + " id=" + _macId);
        if (started != AifError.OK) { SetStatus("could not start " + MacModule); return; }

        try
        {
            // 1) Welcome + look-at-camera prompt (a separate RSR render), then
            //    give the human ~1 s to turn, then capture the face.
            InstructionText.Text = "Welcome to the HCI Multimodal Access Control Service. Look at the camera.";
            SetFace("acquiring face...");
            var speakLook = RenderPromptAsync("Welcome to the HCI Multimodal Access Control Service. Look at the camera.");
            await Task.Delay(TimeSpan.FromSeconds(1));       // .orch: wait 1s
            var face = await CaptureFaceAsync();             // VOA stamps VisualObjectType = Face
            await speakLook;
            Diag("face content: bytes=" + (face?.Data?.Length ?? 0) + " type=" + (face?.VisualQualifier?.Attributes?.VisualObjectType ?? "nil"));

            // 2) Write the face boundary ports and let the Module run. FIR runs;
            //    the Module then SUSPENDS waiting for the speech boundary port.
            var faceBoundary = new Dictionary<string, string>();
            if (face is not null) faceBoundary["FaceObject"] = MpaiJson.ToJson(face);
            faceBoundary["FaceTime"] = MpaiJson.ToJson(NowSimpleTime());

            Diag("write boundary: FaceObject=" + faceBoundary.ContainsKey("FaceObject") + " FaceTime=" + faceBoundary.ContainsKey("FaceTime"));
            var (e1, out1) = await Task.Run(() => _ua!.RunAsync(_macId, faceBoundary).GetAwaiter().GetResult());
            if (e1 != AifError.OK || out1 is null) { SetStatus("run error"); return; }
            Diag("RunAsync -> err=" + e1 + " " + (out1.Suspended ? ("suspended waiting=" + out1.WaitingPort) : "completed"));

            var outcome = out1;

            // 3) On the Module's request for speech, prompt and capture it, then
            //    Resume with the speech boundary ports. SIR -> IDR -> RSR complete.
            if (outcome.Suspended)
            {
                InstructionText.Text = "Speak your passphrase.";
                SetVoice("acquiring speech...");
                await RenderPromptAsync("Speak your passphrase.");
                var speech = await CaptureSpeechAsync();

                var speechBoundary = new Dictionary<string, string>();
                if (speech is not null) speechBoundary["SpeechObject"] = MpaiJson.ToJson(speech);
                speechBoundary["SpeechTime"] = MpaiJson.ToJson(NowSimpleTime());

                Diag("write boundary: SpeechObject=" + speechBoundary.ContainsKey("SpeechObject") + " SpeechTime=" + speechBoundary.ContainsKey("SpeechTime"));
                var (e2, out2) = await Task.Run(() => _ua!.ResumeAsync(_macId, speechBoundary).GetAwaiter().GetResult());
                if (e2 != AifError.OK || out2 is null) { SetStatus("resume error"); return; }
                outcome = out2;
                Diag("ResumeAsync -> err=" + e2 + " " + (out2.Suspended ? ("suspended waiting=" + out2.WaitingPort) : "completed"));
            }

            // 4) Read the boundary outputs.
            var completed = outcome.Completed;
            Diag("completed? " + (completed != null));
            if (completed is null) { SetStatus("the Module did not complete"); return; }

            string? userId = completed.Ports.TryGetValue("UserID", out var uj) ? uj : null;
            byte[]  wav    = Array.Empty<byte>();
            FaceDescriptorsObject? fdo = null;
            if (completed.Ports.TryGetValue("VocalResponse", out var vj) && !string.IsNullOrWhiteSpace(vj))
                wav = MpaiJson.FromJson<BasicSpeechObject>(vj)?.Data ?? Array.Empty<byte>();
            if (completed.Ports.TryGetValue("FaceDescriptors", out var fj) && !string.IsNullOrWhiteSpace(fj))
                fdo = MpaiJson.FromJson<FaceDescriptorsObject>(fj);

            // 5) Present the verdict: the avatar (rendered by the Module's RSR) speaks it.
            await _avatar!.PresentAsync(new SpeakingAvatar(wav, fdo));
            await Task.Delay(TimeSpan.FromSeconds(AvatarUaHost.WavDurationSeconds(wav) + 0.4));

            Diag("outputs: UserID=" + (userId ?? "nil") + " vocalWavBytes=" + wav.Length + " faceDesc=" + (fdo == null ? "nil" : "present"));
            bool granted = !string.IsNullOrWhiteSpace(userId) && !IsCoarse(userId);
            if (granted)
            {
                var name = LabelOf(userId!);
                ShowResult(name is null ? "Access granted" : name + ", welcome", true);
                Diag("verdict: GRANTED " + (name ?? "(no label)"));
                SetStatus("identified");
            }
            else
            {
                ShowResult("Not identified", false);
                Diag("verdict: DENIED");
                SetStatus("not identified");
            }
        }
        finally
        {
            var id = _macId; _macId = -1;
            await Task.Run(() => _ua!.MPAI_AIFU_MODULE_Stop(id));
        }
    }

    // ---- UA real-world I/O limbs -------------------------------------------

    // Capture a webcam frame as a Basic Visual Object, qualified as a Face
    // (VisualObjectType = Face) at acquisition, since MAC has no CVE-VSI stage.
    private async Task<BasicVisualObject?> CaptureFaceAsync()
    {
        try
        {
            var frame = await Task.Run(() =>
                new WebcamVisualAcquisition()
                    .AcquireAsync(new VisualAcquisitionRequest { VisualObjectType = "Face" })
                    .GetAwaiter().GetResult().Data);
            if (frame is { Length: > 0 }) { try { System.IO.File.WriteAllBytes(@"C:\Users\Leonardo\Downloads\last-face.jpg", frame); } catch { } }
            return (frame is { Length: > 0 })
                ? BasicVisualObject.FromFile("webcam.jpg", frame, "Face")
                : null;
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

    // OSD-STM at the current instant (Absolute epoch), from MAC's own clock.
    private static SimpleTime NowSimpleTime()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        return new SimpleTime
        {
            SimpleTimeID = Guid.NewGuid().ToString("N"),
            SimpleTimeData = new List<TimeSegment>
            {
                new TimeSegment
                {
                    FlagsByte = 0, StartTime = now, EndTime = now,
                    AccuracyMode = "single", AccuracyPlusMinus = 0.0, TimeType = true
                }
            }
        };
    }

    // Render a fixed spoken prompt via a separate PAF-RSR Module run (M3167:
    // "a separate RSR render"), with a serious Personal Status.
    private async Task RenderPromptAsync(string words)
    {
        var done = await Task.Run(() => RunRsr(words));
        if (done is null) return;
        byte[] wav = Array.Empty<byte>();
        FaceDescriptorsObject? fdo = null;
        if (done.Ports.TryGetValue("MachineSpeech", out var sj) && !string.IsNullOrWhiteSpace(sj))
            wav = MpaiJson.FromJson<BasicSpeechObject>(sj)?.Data ?? Array.Empty<byte>();
        if (done.Ports.TryGetValue("MachineFaceDescriptors", out var fj) && !string.IsNullOrWhiteSpace(fj))
            fdo = MpaiJson.FromJson<FaceDescriptorsObject>(fj);
        await _avatar!.PresentAsync(new SpeakingAvatar(wav, fdo));
        await Task.Delay(TimeSpan.FromSeconds(AvatarUaHost.WavDurationSeconds(wav) + 0.3));
    }

    // One PAF-RSR run: Start -> write TextObject + PersonalStatus -> read outputs -> Stop.
    private AIF.Controller.Message? RunRsr(string words)
    {
        if (_ua is null) return null;
        if (_ua.MPAI_AIFU_MODULE_Start(RsrModule, _provider!, _settings!, out var rid) != AifError.OK) return null;
        try
        {
            var boundary = new Dictionary<string, string>
            {
                ["TextObject"]     = MpaiJson.ToJson(BasicTextObject.FromText(words)),
                ["PersonalStatus"] = MpaiJson.ToJson(SeriousStatus())
            };
            var (error, outcome) = _ua.RunAsync(rid, boundary).GetAwaiter().GetResult();
            if (error != AifError.OK || outcome?.Completed is null || outcome.Completed.IsError) return null;
            return outcome.Completed;
        }
        finally { _ua.MPAI_AIFU_MODULE_Stop(rid); }
    }

    private static EntityPersonalStatus SeriousStatus() => new()
    {
        TextPersonalStatus = new TextPersonalStatus
        {
            TextEmotion        = Emotion.Of(FactorLabel.Of("CALMNESS", "serious", null, 0.7)),
            TextSocialAttitude = SocialAttitude.Of(FactorLabel.Of("SOCIAL RANK", "serious", null, 0.7))
        }
    };

    // ---- identity helpers ---------------------------------------------------

    private static bool IsCoarse(string userIdJson)
    {
        var label = LabelOf(userIdJson);
        return label is null or "person" or "face" or "speech";
    }

    private static string? LabelOf(string userIdJson)
    {
        try
        {
            var iid = MpaiJson.FromJson<InstanceIdentifier>(userIdJson);
            return iid?.InstanceIdentifierData?.FirstOrDefault()?.InstanceLabel;
        }
        catch { return null; }
    }

    // ---- UI helpers ---------------------------------------------------------

    private void SetStatus(string s) => Dispatcher.Invoke(() => StatusText.Text = s);
    private void SetFace(string s)   => Dispatcher.Invoke(() => FaceStatus.Text = s);
    private void SetVoice(string s)  => Dispatcher.Invoke(() => VoiceStatus.Text = s);

    private void ShowResult(string text, bool granted) => Dispatcher.Invoke(() =>
    {
        ResultText.Text = text;
        ResultText.Foreground = new System.Windows.Media.SolidColorBrush(
            granted ? System.Windows.Media.Color.FromRgb(0x4C, 0xC2, 0x7A)
                    : System.Windows.Media.Color.FromRgb(0xE0, 0x6B, 0x6B));
        ResultBorder.Visibility = Visibility.Visible;
    });

    private void HideResult() => Dispatcher.Invoke(() => ResultBorder.Visibility = Visibility.Collapsed);
}
