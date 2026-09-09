using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;

using AIF.Controller;
using AIF.Store;

using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.UaKit;         // AvatarUaHost
using Mpai.Hci.Api;       // SpeakingAvatar
using Mpai.Mmc.Edp;       // Summary (MMC-SUM)

namespace HciMad;

// HCI-MAD User Agent. Realises UAs/Orchestration/HCI-MAD.orch.
// Roles: (1) real-world I/O limbs - render the avatar, capture the microphone
// (VAD-gated, via AvatarUaHost); (2) orchestration - drive the MMC-MAD-V2.5
// Module through the Controller, one dialogue turn per Start..Stop loop pass.
// No identity, no affect input: EDP therefore emits no machine Personal Status
// and the avatar renders neutrally. Conversation memory is the running Summary,
// which the UA carries from each turn's EditedSummary into the next turn's input.
public partial class MainWindow : Window
{
    private const string MadModule = "MMC-MAD-V2.5";
    private const string RsrModule = "PAF-RSR-V1.6";   // one-shot spoken welcome (not dialogue)

    private static readonly string AmdDir       = Mpai.Core.MpaiPaths.Amds;
    private static readonly string SettingsPath = Mpai.Core.MpaiPaths.Settings;
    private static readonly string AssetsDir    = Mpai.Core.MpaiPaths.Assets;

    private UserAgent?    _ua;
    private MadProvider?  _provider;
    private AimSettings?  _settings;
    private AvatarUaHost? _avatar;

    private volatile bool _running = false;   // set by Start/Stop; the loop watches it
    private string _summary = "";             // running dialogue memory (MMC-SUM text)

    private const string Welcome = "Welcome to the HCI Multimodal Dialogue Service.";
    private const string Closing = "Thank you for using the HCI Multimodal Dialogue Service.";

    // ---- Diagnostics: FOLLOW THE CONTENT. Toggle with DiagOn. -------------
    private static bool DiagOn = true;
    private static void Diag(string s)
    {
        if (!DiagOn) return;
        try { System.IO.File.AppendAllText(@"C:\Users\Leonardo\Downloads\mad-diag.log",
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
            await Task.Delay(TimeSpan.FromSeconds(2.0));   // scene settle

            await Task.Run(() =>
            {
                var store = new AmdStore(AmdDir); store.Scan();
                _settings = AimSettings.Load(SettingsPath);
                _provider = new MadProvider(store);
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

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_ua is null || _avatar is null || _running) return;
        _running = true;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        _summary = "";
        InstructionText.Text = "Listening... speak, then pause. Press Stop to end.";
        _ = Task.Run(LoopAsync);   // run the conversation loop off the UI thread
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _running = false;          // the loop exits after the current turn
        StopButton.IsEnabled = false;
        InstructionText.Text = "Conversation closed. Press Start to begin again.";
        SetStatus("stopped");
        StartButton.IsEnabled = true;
        _ = Task.Run(async () => { await RenderPromptAsync(Closing); });   // closing on Stop
    }

    // The HCI-MAD.orch loop: each pass is one dialogue turn - capture the user's
    // speech (VAD end-of-utterance), run the Module (ASR -> EDP -> RSR), speak the
    // reply, and carry the EditedSummary forward as the next turn's Summary.
    private async Task LoopAsync()
    {
        try
        {
            await RenderPromptAsync(Welcome);   // spoken once on Start
            while (_running)
            {
                SetTurn("listening...");
                var speech = await CaptureSpeechAsync();
                if (!_running) break;
                if (speech is null || speech.Data is null || speech.Data.Length == 0)
                {
                    Diag("empty capture; continue");
                    continue;
                }

                var reply = await Task.Run(() => RunTurn(speech));
                if (reply is null) { SetTurn("(no reply)"); continue; }

                await _avatar!.PresentAsync(new SpeakingAvatar(reply.Value.wav, reply.Value.fdo));
                await Task.Delay(TimeSpan.FromSeconds(AvatarUaHost.WavDurationSeconds(reply.Value.wav) + 0.3));
                _summary = reply.Value.summary;   // memory-carry
                SetTurn("your turn...");
            }
        }
        catch (Exception ex) { Program.Record("loop", ex); Diag("loop error: " + ex.Message); }
    }

    // One dialogue turn: Start MMC-MAD -> RunAsync{InputSpeech,InputSpeechTime,Summary}
    // -> read MachineSpeech + MachineFaceDescriptors + EditedSummary -> Stop.
    private (byte[] wav, FaceDescriptorsObject? fdo, string summary)? RunTurn(BasicSpeechObject speech)
    {
        if (_ua is null) return null;
        if (_ua.MPAI_AIFU_MODULE_Start(MadModule, _provider!, _settings!, out var id) != AifError.OK)
        { Diag("MAD start failed"); return null; }
        try
        {
            var boundary = new Dictionary<string, string>
            {
                ["InputSpeech"]     = MpaiJson.ToJson(speech),
                ["InputSpeechTime"] = MpaiJson.ToJson(NowSimpleTime()),
                ["Summary"]         = MpaiJson.ToJson(Summary.Of(_summary))
            };
            var (err, outcome) = _ua.RunAsync(id, boundary).GetAwaiter().GetResult();
            if (err != AifError.OK || outcome?.Completed is null || outcome.Completed.IsError)
            { Diag("MAD run err=" + err); return null; }

            var c = outcome.Completed;
            byte[] wav = Array.Empty<byte>();
            FaceDescriptorsObject? fdo = null;
            string summary = _summary;
            if (c.Ports.TryGetValue("MachineSpeech", out var sj) && !string.IsNullOrWhiteSpace(sj))
                wav = MpaiJson.FromJson<BasicSpeechObject>(sj)?.Data ?? Array.Empty<byte>();
            if (c.Ports.TryGetValue("MachineFaceDescriptors", out var fj) && !string.IsNullOrWhiteSpace(fj))
                fdo = MpaiJson.FromJson<FaceDescriptorsObject>(fj);
            if (c.Ports.TryGetValue("EditedSummary", out var suj) && !string.IsNullOrWhiteSpace(suj))
                summary = MpaiJson.FromJson<Summary>(suj)?.Text() ?? summary;
            Diag("turn: wavBytes=" + wav.Length + " faceDesc=" + (fdo == null ? "nil" : "present"));
            return (wav, fdo, summary);
        }
        finally { _ua.MPAI_AIFU_MODULE_Stop(id); }
    }

    // ---- UA I/O limbs -------------------------------------------------------

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
                new TimeSegment { FlagsByte = 0, StartTime = now, EndTime = now,
                                  AccuracyMode = "single", AccuracyPlusMinus = 0.0, TimeType = true }
            }
        };
    }

    // One-shot spoken prompt via PAF-RSR (NEUTRAL - MAD is affect-free): the welcome.
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

    // One PAF-RSR run: Start -> write TextObject (no PersonalStatus -> neutral) -> read outputs -> Stop.
    private AIF.Controller.Message? RunRsr(string words)
    {
        if (_ua is null) return null;
        if (_ua.MPAI_AIFU_MODULE_Start(RsrModule, _provider!, _settings!, out var rid) != AifError.OK) return null;
        try
        {
            var boundary = new Dictionary<string, string>
            {
                ["TextObject"] = MpaiJson.ToJson(BasicTextObject.FromText(words))
                // no PersonalStatus -> RSR renders neutrally (MAD is affect-free)
            };
            var (error, outcome) = _ua.RunAsync(rid, boundary).GetAwaiter().GetResult();
            if (error != AifError.OK || outcome?.Completed is null || outcome.Completed.IsError) return null;
            return outcome.Completed;
        }
        finally { _ua.MPAI_AIFU_MODULE_Stop(rid); }
    }

    // ---- UI helpers ---------------------------------------------------------
    private void SetStatus(string s) => Dispatcher.Invoke(() => StatusText.Text = s);
    private void SetTurn(string s)   => Dispatcher.Invoke(() => TurnStatus.Text = s);
}
