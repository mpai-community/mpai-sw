using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;

using AIF.Controller;   // AifError
using AIF.Store;        // AmdStore (provider factory)

using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.UaKit;         // AvatarUaHost
using Mpai.Hci.Api;       // NorthApi, SpeakingAvatar

namespace HciMad;

// HCI-MAD User Agent - drives MMC-MAD through the type-addressed North API.
// The UA identifies data ONLY by data type. One dialogue turn per loop pass:
// supply OSD-BSO (speech) + OSD-STM (time); read OSD-BSO (reply, spoken) and
// PAF-FDO (avatar). The Module lives Start..Stop so EDP keeps the running
// Summary as memory across turns. No identity, no affect: neutral avatar.
public partial class MainWindow : Window
{
    private const string MadModule = "MMC-MAD-V2.5";
    private const string RsrModule = "PAF-RSR-V1.6";

    private const string BSO = "OSD-BSO-V1.5";   // speech object
    private const string STM = "OSD-STM-V1.5";   // acquisition time
    private const string BTO = "OSD-BTO-V1.5";   // text object (welcome/closing)
    private const string FDO = "PAF-FDO-V1.6";   // face descriptors

    private static readonly string AmdDir       = Mpai.Core.MpaiPaths.Amds;
    private static readonly string SettingsPath = Mpai.Core.MpaiPaths.Settings;
    private static readonly string AssetsDir    = Mpai.Core.MpaiPaths.Assets;

    private NorthApi?     _north;
    private AvatarUaHost? _avatar;
    private volatile bool _running = false;

    private const string Welcome = "Welcome to the HCI Multimodal Dialogue Service.";
    private const string Closing = "Thank you for using the HCI Multimodal Dialogue Service.";

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
                _north = new NorthApi(AmdDir, SettingsPath, store => new MadProvider(store)));

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
        if (_north is null || _avatar is null || _running) return;
        if (_north.StartFlow(MadModule) != AifError.OK)
        { SetStatus("could not start " + MadModule); return; }
        _running = true;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        InstructionText.Text = "Listening... speak, then pause. Press Stop to end.";
        _ = Task.Run(LoopAsync);
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _running = false;
        StopButton.IsEnabled = false;
        InstructionText.Text = "Conversation closed. Press Start to begin again.";
        SetStatus("stopped");
        StartButton.IsEnabled = true;
        _ = Task.Run(async () =>
        {
            await RenderPromptAsync(Closing);
            _north?.StopFlow(MadModule);   // module lived Start..Stop; EDP kept memory
        });
    }

    private async Task LoopAsync()
    {
        try
        {
            await RenderPromptAsync(Welcome);
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
                SetTurn("your turn...");
            }
        }
        catch (Exception ex) { Program.Record("loop", ex); Diag("loop error: " + ex.Message); }
    }

    // One dialogue turn: supply OSD-BSO (speech) + OSD-STM (time); read OSD-BSO
    // (reply) + PAF-FDO (avatar). The Module stays alive; EDP carries the memory.
    private (byte[] wav, FaceDescriptorsObject? fdo)? RunTurn(BasicSpeechObject speech)
    {
        if (_north is null) return null;
        var inputs = new List<NorthApi.Datum>
        {
            new NorthApi.Datum(BSO, MpaiJson.ToJson(speech)),
            new NorthApi.Datum(STM, MpaiJson.ToJson(NowSimpleTime()))
        };
        var r = _north.Advance(MadModule, inputs);
        if (!r.Ok) { Diag("MAD turn err=" + r.Error); return null; }

        byte[] wav = Array.Empty<byte>();
        var sj = r.ByType(BSO);
        if (!string.IsNullOrWhiteSpace(sj)) wav = MpaiJson.FromJson<BasicSpeechObject>(sj)?.Data ?? Array.Empty<byte>();
        FaceDescriptorsObject? fdo = null;
        var fj = r.ByType(FDO);
        if (!string.IsNullOrWhiteSpace(fj)) fdo = MpaiJson.FromJson<FaceDescriptorsObject>(fj);
        Diag("turn: wavBytes=" + wav.Length + " faceDesc=" + (fdo == null ? "nil" : "present"));
        return (wav, fdo);
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
                new TimeSegment { FlagsByte = 0, StartTime = now, EndTime = now,
                                  AccuracyMode = "single", AccuracyPlusMinus = 0.0, TimeType = true }
            }
        };
    }

    // One-shot neutral spoken prompt via PAF-RSR (no PersonalStatus).
    private async Task RenderPromptAsync(string words)
    {
        if (_north is null) return;
        var inputs = new List<NorthApi.Datum>
        {
            new NorthApi.Datum(BTO, MpaiJson.ToJson(BasicTextObject.FromText(words)))
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

    private void SetStatus(string s) => Dispatcher.Invoke(() => StatusText.Text = s);
    private void SetTurn(string s)   => Dispatcher.Invoke(() => TurnStatus.Text = s);
}