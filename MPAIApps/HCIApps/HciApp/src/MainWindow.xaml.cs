using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

using AIF.Controller;
using AIF.Store;

using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Aims.Visual;
using Mpai.UaKit;
using Mpai.Hci.Api;

namespace HciApp;

// Human-CAV Interaction User Agent. The UA captures the AUDIO scene (BAO) and a
// face (BVO) and hands them to the MMC-HCI Module through the North API. The
// Module's front end (BAS/AVA/ASI) discriminates audio from speech: ASI scans the
// audio objects, converts a speech object (BAO -> BSO) and feeds ASR/SIR/PSE.
// There is NO boundary speech input; the recogniser is always fed by ASI. The UA
// captures sound; the Module decides what it is.
//
// Flow: Start -> "ride?" -> front-end runs (audio+face) until a face AND a speech
// are captured -> yes: identify (MAC pipeline); no: "another occasion" -> stop.
// Identify ok -> converse (MPD). "stop" -> pause. Start -> resume.
public partial class MainWindow : Window
{
    private const string HciModule = "MMC-HCI-V2.5";
    private const string RsrModule = "PAF-RSR-V1.6";

    private const string BAO = "OSD-BAO-V1.5";
    private const string BVO = "OSD-BVO-V1.5";
    private const string BLO = "OSD-BLO-V1.5";
    private const string BSO = "OSD-BSO-V1.5";
    private const string BTO = "OSD-BTO-V1.5";
    private const string FDO = "PAF-FDO-V1.6";
    private const string IID = "OSD-IID-V1.5";

    private static readonly string AmdDir       = Mpai.Core.MpaiPaths.Amds;
    private static readonly string SettingsPath = Mpai.Core.MpaiPaths.Settings;
    private static readonly string AssetsDir    = Mpai.Core.MpaiPaths.Assets;
    private static readonly string GalleryJson  = Mpai.Core.MpaiPaths.Gallery;

    private const string Welcome =
        "Welcome to the M P A I Human C A V Interaction service. Please wait a moment while the components load.";
    private const string RideQuestion =
        "Do you want a ride on the M P A I Connected Autonomous Vehicle?";
    private const string AnotherOccasion = "Maybe on another occasion.";
    private const string NotEligible = "I am sorry, you are not eligible to run the M P A I C A V.";
    private const string ConversePrompt =
        "While we have a comfortable travel, let us have a conversation. Say stop when you want to end it.";

    private NorthApi?     _north;
    private AvatarUaHost? _avatar;
    private bool _ready, _started, _identified;
    private string? _userName;
    private volatile bool _conversing;
    private int _phase = 0;

    private static void Diag(string s)
    {
        try { System.IO.File.AppendAllText(@"C:\Users\Leonardo\Downloads\hci-diag.log",
              DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + System.Environment.NewLine); } catch { }
    }

    public MainWindow() { InitializeComponent(); Loaded += OnLoaded; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            SetStatus("loading...");
            _avatar = new AvatarUaHost(Web, Dispatcher, AmdDir, AssetsDir);
            await _avatar.InitAsync();
            await Task.Run(() => _north = new NorthApi(AmdDir, SettingsPath, store => new HciProvider(store, GalleryJson)));
            _ready = true;
            InstructionText.Text = "Press Start to meet the CAV.";
            SetStatus("Ready."); StartButton.IsEnabled = true;
        }
        catch (Exception fatal) { Program.Record("startup", fatal); SetStatus("startup failed: " + fatal.Message); }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _north is null || _avatar is null) return;
        StartButton.IsEnabled = false;
        try
        {
            if (_phase == 0)      await BeginAsync();
            else if (_phase == 2) await ResumeConversationAsync();
        }
        catch (Exception ex) { Program.Record("flow", ex); SetStatus("error: " + ex.Message); }
        finally { StartButton.IsEnabled = true; }
    }

    private async Task BeginAsync()
    {
        SetStatus("welcome...");
        await RenderPromptAsync(Welcome);
        var started = await Task.Run(() => _north!.StartFlow(HciModule));
        Diag("StartFlow HCI -> " + started);
        if (started != AifError.OK) { SetStatus("could not start " + HciModule); return; }

        HideResult();
        InstructionText.Text = "Answer yes or no.";
        await RenderPromptAsync(RideQuestion);

        // Step 3: run the front-end (audio scene + face) until a face AND a speech
        // are captured. ASI discriminates; ASR transcribes; the transcript is the
        // user's answer.
        SetStatus("listening...");
        string? answer = null;
        bool faceSeen = false;
        for (int attempt = 1; attempt <= 4 && string.IsNullOrWhiteSpace(answer); attempt++)
        {
            var (recognised, seen) = await FrontEndTurnAsync();
            faceSeen = seen;
            Diag($"ride attempt {attempt}: face={seen} recognised='{recognised ?? ""}'");
            if (seen && !string.IsNullOrWhiteSpace(recognised)) answer = recognised;
        }
        Diag("ride answer='" + (answer ?? "") + "'");

        if (string.IsNullOrWhiteSpace(answer) || IsNo(answer))
        {
            await RenderPromptAsync(AnotherOccasion);
            SetStatus("declined."); await StopHciAsync(); return;
        }

        // Step 6: identify (MAC-style). Still the front-end audio path (ASI->SIR).
        InstructionText.Text = "Look at the camera.";
        SetFace("acquiring...");
        var lookPrompt = RenderPromptAsync("Look at the camera.");
        await Task.Delay(TimeSpan.FromSeconds(1));
        var (idRecognised, idFaceSeen) = await FrontEndTurnAsync();   // face + passphrase in one perceive
        await lookPrompt;
        await RenderPromptAsync("Speak your passphrase.");
        var (idRecognised2, _) = await FrontEndTurnAsync();

        // The identity (OSD-IID) is produced by IDR from FIR+SIR; read it.
        var userId = _lastUserId;
        _userName = CleanId(userId);
        Diag("identity userId='" + (userId ?? "") + "' name='" + (_userName ?? "") + "'");

        if (!IsEligible(_userName))
        {
            ShowResult("Not identified", granted: false);
            await RenderPromptAsync(NotEligible);
            SetStatus("not eligible."); await StopHciAsync(); return;
        }

        ShowResult("Welcome, " + _userName, granted: true);
        _identified = true; _phase = 2;
        await RenderPromptAsync("Welcome " + _userName + ". " + ConversePrompt);
        await ConverseLoopAsync();
    }

    private async Task ResumeConversationAsync()
    {
        if (!_identified) return;
        await RenderPromptAsync("Let us continue our conversation.");
        await ConverseLoopAsync();
    }

    private async Task ConverseLoopAsync()
    {
        _conversing = true;
        InstructionText.Text = "Speak. Say stop to pause.";
        while (_conversing)
        {
            SetVoice("listening...");
            var (said, _) = await FrontEndTurnAsync();
            Diag("converse said='" + (said ?? "") + "'");
            if (string.IsNullOrWhiteSpace(said)) continue;
            if (IsStop(said))
            {
                _conversing = false;
                await RenderPromptAsync("Conversation paused. Press Start to continue.");
                SetStatus("paused."); InstructionText.Text = "Press Start to resume.";
                return;
            }
            if (_lastReplyWav.Length > 0 || _lastFdo is not null)
            {
                await _avatar!.PresentAsync(new SpeakingAvatar(_lastReplyWav, _lastFdo));
                await Task.Delay(TimeSpan.FromSeconds(AvatarUaHost.WavDurationSeconds(_lastReplyWav) + 0.3));
            }
            SetVoice("your turn...");
        }
    }

    // Outputs read from the most recent front-end Advance.
    private string? _lastUserId;
    private byte[]  _lastReplyWav = Array.Empty<byte>();
    private FaceDescriptorsObject? _lastFdo;

    // One front-end turn: supply the AUDIO scene (BAO) + face (BVO) + lidar; the
    // Module's front end describes and discriminates. Returns the recognised text
    // (from ASR, via ASI) and whether a face was captured. Also stashes the
    // identity, reply speech and face descriptors from this Advance.
    private async Task<(string? recognised, bool faceSeen)> FrontEndTurnAsync()
    {
        var inputs = new List<NorthApi.Datum>();
        var audio = await Task.Run(() => _avatar!.CaptureAudio(6.0));
        try { var __ia=audio?.BasicAudioObjectData?.OfType<InlineAudioData>()?.FirstOrDefault(); int __r=(audio?.AudioQualifier?.Formats?.ContentFormat?.RawData?.SampleSpace?.SamplingFrequency is double __f && __f>0)?(int)__f:16000; Mpai.Core.MpaiDiag.DumpB64(__ia?.Data, __r, "1_AOA_out"); } catch {}   // audio scene (BAO)
        if (audio is not null) inputs.Add(new NorthApi.Datum(BAO, MpaiJson.ToJson(audio)));
        var face = await Task.Run(() => CaptureFace());
        if (face is not null) inputs.Add(new NorthApi.Datum(BVO, MpaiJson.ToJson(face)));
        inputs.Add(new NorthApi.Datum(BLO, MpaiJson.ToJson(new BasicLiDARObject
        { BasicLiDARObjectID = Guid.NewGuid().ToString("N"), BasicLiDARData = new List<object>() })));

        var r = await Task.Run(() => _north!.Advance(HciModule, inputs));
        if (!r.Ok) { Diag("front-end Advance err=" + r.Error); return (null, face is not null); }

        _lastUserId   = r.ByType(IID);
        _lastReplyWav = SpeechOf(r.ByType(BSO));
        _lastFdo      = FdoOf(r.ByType(FDO));
        string? recognised = TextOf(r.ByType(BTO, 2));
        return (recognised, face is not null);
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

    private async Task RenderPromptAsync(string words)
    {
        if (_north is null) return;
        var inputs = new List<NorthApi.Datum> { new NorthApi.Datum(BTO, MpaiJson.ToJson(BasicTextObject.FromText(words))) };
        var r = await Task.Run(() => _north!.Advance(RsrModule, inputs));
        if (!r.Ok) return;
        byte[] wav = SpeechOf(r.ByType(BSO));
        FaceDescriptorsObject? fdo = FdoOf(r.ByType(FDO));
        await _avatar!.PresentAsync(new SpeakingAvatar(wav, fdo));
        await Task.Delay(TimeSpan.FromSeconds(AvatarUaHost.WavDurationSeconds(wav) + 0.3));
    }

    private async Task StopHciAsync()
    {
        try { if (_started) { await Task.Run(() => _north!.StopFlow(HciModule)); _started = false; _identified = false; _conversing = false; } } catch { }
        _phase = 0;
    }

    private static bool IsNo(string? t)   => Has(t, "no", "not", "dont", "do not", "nope", "never", "another time");
    private static bool IsStop(string? t) => Has(t, "stop", "quit", "end", "enough", "goodbye", "bye");

    private static bool Has(string? t, params string[] words)
    {
        if (string.IsNullOrWhiteSpace(t)) return false;
        var tokens = System.Text.RegularExpressions.Regex.Split(t.ToLowerInvariant(), "[^a-z]+").Where(s => s.Length > 0).ToArray();
        var joined = " " + string.Join(" ", tokens) + " ";
        foreach (var w in words)
        {
            if (w.Contains(' ')) { if (joined.Contains(" " + w + " ")) return true; }
            else if (tokens.Contains(w)) return true;
        }
        return false;
    }

    private static string? CleanId(string? iidJson)
    {
        if (string.IsNullOrWhiteSpace(iidJson)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(iidJson, "subject:([A-Za-z0-9_\\-]+)");
        if (m.Success) return m.Groups[1].Value.Replace('_', ' ').Trim();
        var m2 = System.Text.RegularExpressions.Regex.Match(iidJson, "\"InstanceLabel\"\\s*:\\s*\"([^\"]+)\"");
        return m2.Success ? m2.Groups[1].Value.Trim() : null;
    }

    private static bool IsEligible(string? name) => !string.IsNullOrWhiteSpace(name);

    private static string? TextOf(string? json)
    { if (string.IsNullOrWhiteSpace(json)) return null; try { return MpaiJson.FromJson<BasicTextObject>(json)?.GetText(); } catch { return null; } }
    private static byte[] SpeechOf(string? json)
    { if (string.IsNullOrWhiteSpace(json)) return Array.Empty<byte>(); try { return MpaiJson.FromJson<BasicSpeechObject>(json)?.Data ?? Array.Empty<byte>(); } catch { return Array.Empty<byte>(); } }
    private static FaceDescriptorsObject? FdoOf(string? json)
    { if (string.IsNullOrWhiteSpace(json)) return null; try { return MpaiJson.FromJson<FaceDescriptorsObject>(json); } catch { return null; } }

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

    protected override void OnClosed(EventArgs e)
    { try { if (_started) _north?.StopFlow(HciModule); } catch { } base.OnClosed(e); }
}
