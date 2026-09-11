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

namespace HciMac;

// HCI-MAC User Agent - drives MMC-MAC through the type-addressed North API.
// The UA identifies data ONLY by data type: supply OSD-BVO (face) -> the flow
// suspends -> supply OSD-BSO (speech) -> read OSD-BTO (Response, banner),
// OSD-BSO (VocalResponse, speak), PAF-FDO (FaceDescriptors, avatar). No names.
public partial class MainWindow : Window
{
    private const string MacModule = "MMC-MAC-V2.5";
    private const string RsrModule = "PAF-RSR-V1.6";

    private const string BVO = "OSD-BVO-V1.5";
    private const string BSO = "OSD-BSO-V1.5";
    private const string BTO = "OSD-BTO-V1.5";
    private const string EPS = "MMC-EPS-V2.5";
    private const string FDO = "PAF-FDO-V1.6";

    private static readonly string AmdDir       = Mpai.Core.MpaiPaths.Amds;
    private static readonly string SettingsPath = Mpai.Core.MpaiPaths.Settings;
    private static readonly string AssetsDir    = Mpai.Core.MpaiPaths.Assets;
    private static readonly string GalleryJson  = Mpai.Core.MpaiPaths.Gallery;

    private NorthApi?     _north;
    private AvatarUaHost? _avatar;

    private static void Diag(string s)
    {
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
                _north = new NorthApi(AmdDir, SettingsPath, store => new MacProvider(store, GalleryJson)));

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
        if (_north is null || _avatar is null) return;
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

    private async Task RunFlowAsync()
    {
        var started = await Task.Run(() => _north!.StartFlow(MacModule));
        Diag("StartFlow " + MacModule + " -> " + started);
        if (started != AifError.OK) { SetStatus("could not start " + MacModule); return; }

        try
        {
            InstructionText.Text = "Welcome to the HCI Multimodal Access Control Service. Look at the camera.";
            SetFace("acquiring face...");
            var speakLook = RenderPromptAsync("Welcome to the HCI Multimodal Access Control Service. Look at the camera.");
            await Task.Delay(TimeSpan.FromSeconds(1));
            var face = await CaptureFaceAsync();
            await speakLook;
            Diag("face bytes=" + (face?.Data?.Length ?? 0));

            var faceIn = new List<NorthApi.Datum>();
            if (face is not null) faceIn.Add(new NorthApi.Datum(BVO, MpaiJson.ToJson(face)));
            var r1 = await Task.Run(() => _north!.Advance(MacModule, faceIn));
            Diag("Advance(face) -> err=" + r1.Error + (r1.Suspended ? " suspended" : " completed"));
            if (!r1.Ok) { SetStatus("run error"); return; }

            var result = r1;

            if (r1.Suspended)
            {
                InstructionText.Text = "Speak your passphrase.";
                SetVoice("acquiring speech...");
                await RenderPromptAsync("Speak your passphrase.");
                var speech = await CaptureSpeechAsync();

                var speechIn = new List<NorthApi.Datum>();
                if (speech is not null) speechIn.Add(new NorthApi.Datum(BSO, MpaiJson.ToJson(speech)));
                var r2 = await Task.Run(() => _north!.Advance(MacModule, speechIn));
                Diag("Advance(speech) -> err=" + r2.Error + (r2.Suspended ? " suspended" : " completed"));
                if (!r2.Ok) { SetStatus("resume error"); return; }
                result = r2;
            }

            string? responseText = null;
            var rj = result.ByType(BTO);
            if (!string.IsNullOrWhiteSpace(rj)) responseText = MpaiJson.FromJson<BasicTextObject>(rj)?.GetText();

            byte[] wav = Array.Empty<byte>();
            var vj = result.ByType(BSO);
            if (!string.IsNullOrWhiteSpace(vj)) wav = MpaiJson.FromJson<BasicSpeechObject>(vj)?.Data ?? Array.Empty<byte>();

            FaceDescriptorsObject? fdo = null;
            var fj = result.ByType(FDO);
            if (!string.IsNullOrWhiteSpace(fj)) fdo = MpaiJson.FromJson<FaceDescriptorsObject>(fj);

            await _avatar!.PresentAsync(new SpeakingAvatar(wav, fdo));
            await Task.Delay(TimeSpan.FromSeconds(AvatarUaHost.WavDurationSeconds(wav) + 0.4));

            bool granted = responseText is not null &&
                           responseText.IndexOf("granted", System.StringComparison.OrdinalIgnoreCase) >= 0;
            string banner = string.IsNullOrWhiteSpace(responseText)
                ? (granted ? "Access granted" : "Not identified")
                : responseText;
            ShowResult(banner, granted);
            Diag("outputs: response=" + (responseText ?? "nil") + " wav=" + wav.Length + " fdo=" + (fdo == null ? "nil" : "present"));
            SetStatus(granted ? "identified" : "not identified");
        }
        finally
        {
            await Task.Run(() => _north!.StopFlow(MacModule));
        }
    }

    private async Task<BasicVisualObject?> CaptureFaceAsync()
    {
        try
        {
            var frame = await Task.Run(() =>
                new WebcamVisualAcquisition()
                    .AcquireAsync(new VisualAcquisitionRequest { VisualObjectType = "Face" })
                    .GetAwaiter().GetResult().Data);
            if (frame is { Length: > 0 }) { try { System.IO.File.WriteAllBytes(@"C:\Users\Leonardo\Downloads\last-face.jpg", frame); } catch { } }
            return (frame is { Length: > 0 }) ? BasicVisualObject.FromFile("webcam.jpg", frame, "Face") : null;
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
        var fj = r.ByType(FDO);
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