using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using AIF.Controller;   // AifError
using AIF.Store;        // AmdStore (provider factory)

using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.UaKit;         // AvatarUaHost
using Mpai.Hci.Api;       // NorthApi, SpeakingAvatar

namespace HciMat;

// HCI-MAT User Agent - drives MMC-MAT through the type-addressed North API.
// The UA identifies data ONLY by data type. It provides an input language, an
// output language (a Language Selector, OSD-SEL) and either a Text Object
// (OSD-BTO) or a Speech Object (OSD-BSO), and reads back TranslatedText (OSD-BTO),
// MachineSpeech (OSD-BSO) and MachineFaceDescriptors (PAF-FDO). No port names.
// Two-press Start -> Select -> (type+Enter => see text | Speak => hear it) -> Stop.
public partial class MainWindow : Window
{
    private const string MatModule = "MMC-MAT-V2.5";
    private const string RsrModule = "PAF-RSR-V1.6";

    private const string BSO = "OSD-BSO-V1.5";   // speech object
    private const string STM = "OSD-STM-V1.5";   // acquisition time
    private const string SEL = "OSD-SEL-V1.5";   // language selector (from/to)
    private const string BTO = "OSD-BTO-V1.5";   // text object (input) / TranslatedText (output)
    private const string FDO = "PAF-FDO-V1.6";   // face descriptors

    private static readonly string AmdDir       = Mpai.Core.MpaiPaths.Amds;
    private static readonly string SettingsPath = Mpai.Core.MpaiPaths.Settings;
    private static readonly string AssetsDir    = Mpai.Core.MpaiPaths.Assets;

    private static readonly (string Code, string Name)[] Languages =
    {
        ("en","English"), ("it","Italiano"), ("es","Espanol"), ("pt","Portugues"),
        ("fr","Francais"), ("de","Deutsch"), ("ja","Nihongo"), ("zh","Zhongwen")
    };

    private NorthApi?     _north;
    private AvatarUaHost? _avatar;
    private volatile bool _busy = false;
    private int _phase = 0;   // 0=before first Start, 1=models loaded, 2=Select active
    private bool _matStarted = false;

    private const string WelcomeLoading =
        "Welcome to the Multimodal Translation Service. Please wait a few seconds while the models load.";
    private const string Instructions =
        "Press Select to choose the input and output languages. " +
        "Type text and press Enter to obtain a text translation. " +
        "Press Speak to obtain a speech translation. After hearing the translation of your speech, press Stop. " +
        "If you want another translation, repeat what you did with the first one.";

    private static void Diag(string s)
    {
        try { System.IO.File.AppendAllText(@"C:\Users\Leonardo\Downloads\mat-diag.log",
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
            foreach (var (code, name) in Languages)
            {
                FromLang.Items.Add(new ComboBoxItem { Content = name, Tag = code });
                ToLang.Items.Add(new ComboBoxItem { Content = name, Tag = code });
            }
            FromLang.SelectedIndex = 0;   // English
            ToLang.SelectedIndex = 1;     // Italiano

            _avatar = new AvatarUaHost(Web, Dispatcher, AmdDir, AssetsDir);
            await _avatar.InitAsync();
            await Task.Delay(TimeSpan.FromSeconds(2.0));

            await Task.Run(() =>
                _north = new NorthApi(AmdDir, SettingsPath, store => new MatProvider(store)));

            SetStatus("Press Start.");
            PrimaryButton.IsEnabled = true;
        }
        catch (Exception fatal) { App.Log("startup", fatal); SetStatus("startup failed: " + fatal.Message); }
    }

    private string FromCode() => Dispatcher.Invoke(() => (FromLang.SelectedItem as ComboBoxItem)?.Tag as string ?? "en");
    private string ToCode()   => Dispatcher.Invoke(() => (ToLang.SelectedItem   as ComboBoxItem)?.Tag as string ?? "it");

    private async void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_north is null || _avatar is null || _busy) return;

        if (_phase == 0)   // ---- FIRST Start press ----
        {
            PrimaryButton.IsEnabled = false;
            SetStatus("welcome...");
            await RenderPromptAsync(WelcomeLoading);
            SetStatus("loading models...");
            var started = await Task.Run(() => _north!.StartFlow(MatModule));
            if (started != AifError.OK) { SetStatus("could not start " + MatModule); return; }
            _matStarted = true;
            _phase = 1;
            PrimaryButton.IsEnabled = true;
            SetStatus("Models ready. Press Start again.");
            return;
        }

        if (_phase == 1)   // ---- SECOND Start press ----
        {
            PrimaryButton.IsEnabled = false;
            SetStatus("instructions...");
            await RenderPromptAsync(Instructions);
            PrimaryButton.Content = "Select";
            PrimaryButton.IsEnabled = true;
            SpeakButton.Visibility = Visibility.Visible; SpeakButton.IsEnabled = false;
            StopButton.Visibility  = Visibility.Visible;
            _phase = 2;
            SetStatus("Press Select to choose input and output languages.");
            return;
        }

        // ---- phase 2: this press is SELECT ----
        SelectPanel.Visibility = Visibility.Visible;
        SpeakButton.IsEnabled = true;
        SetStatus("Languages set: " + FromCode() + " -> " + ToCode() + ". Type + Enter, or press Speak.");
    }

    // Typed path: text + Enter -> Module translates -> display TranslatedText.
    private async void TypeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _busy || !_matStarted) return;
        string typed = TypeBox.Text?.Trim() ?? "";
        if (typed.Length == 0) return;
        _busy = true; SetTurn("translating...");
        var reply = await Task.Run(() => RunTurn(speech: null, typedText: typed, from: FromCode(), to: ToCode()));
        if (reply is not null && !string.IsNullOrWhiteSpace(reply.Value.text))
            TranslationText.Text = reply.Value.text;
        SetTurn("");
        _busy = false;
    }

    // Speak path: Speak -> speak -> Module translates -> lady speaks it.
    private async void SpeakButton_Click(object sender, RoutedEventArgs e)
    {
        if (_north is null || _avatar is null || _busy || !_matStarted) return;
        _busy = true; SpeakButton.IsEnabled = false; PrimaryButton.IsEnabled = false;
        SetTurn("listening... speak, then pause.");
        var speech = await CaptureSpeechAsync(FromCode());
        if (speech is null || speech.Data is null || speech.Data.Length == 0)
        { SetTurn("(nothing captured) - press Stop"); _busy = false; return; }

        SetTurn("translating...");
        var reply = await Task.Run(() => RunTurn(speech: speech, typedText: null, from: FromCode(), to: ToCode()));
        if (reply is not null)
        {
            if (!string.IsNullOrWhiteSpace(reply.Value.text)) TranslationText.Text = reply.Value.text;
            await _avatar!.PresentAsync(new SpeakingAvatar(reply.Value.wav, reply.Value.fdo));
            await Task.Delay(TimeSpan.FromSeconds(AvatarUaHost.WavDurationSeconds(reply.Value.wav) + 0.3));
        }
        SetTurn("press Stop to continue.");
        _busy = false;
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        SetTurn("");
        PrimaryButton.IsEnabled = true;
        SpeakButton.IsEnabled = true;
        SetStatus("Ready. Type + Enter, or press Speak. (Select to change languages.)");
    }

    // One Module run, addressed by data type: LanguageSelector (OSD-SEL) always;
    // typed => TextObject (OSD-BTO); spoken => InputSpeech (OSD-BSO) + time (OSD-STM).
    // Read TranslatedText (OSD-BTO), MachineSpeech (OSD-BSO), FaceDescriptors (PAF-FDO).
    private (byte[] wav, FaceDescriptorsObject? fdo, string text)? RunTurn(
        BasicSpeechObject? speech, string? typedText, string from, string to)
    {
        if (_north is null || !_matStarted) return null;

        var selector = BasicSelectorObject.Languages(from, to);
        var inputs = new List<NorthApi.Datum> { new NorthApi.Datum(SEL, MpaiJson.ToJson(selector)) };
        if (typedText is not null)
            inputs.Add(new NorthApi.Datum(BTO, MpaiJson.ToJson(BasicTextObject.FromText(typedText))));
        else if (speech is not null)
        {
            inputs.Add(new NorthApi.Datum(BSO, MpaiJson.ToJson(speech)));
            inputs.Add(new NorthApi.Datum(STM, MpaiJson.ToJson(NowSimpleTime())));
        }

        var r = _north.Advance(MatModule, inputs);
        if (!r.Ok) { Diag("MAT run err=" + r.Error); return null; }

        byte[] wav = Array.Empty<byte>();
        var sj = r.ByType(BSO);
        if (!string.IsNullOrWhiteSpace(sj)) wav = MpaiJson.FromJson<BasicSpeechObject>(sj)?.Data ?? Array.Empty<byte>();
        FaceDescriptorsObject? fdo = null;
        var fj = r.ByType(FDO);
        if (!string.IsNullOrWhiteSpace(fj)) fdo = MpaiJson.FromJson<FaceDescriptorsObject>(fj);
        string text = "";
        var tj = r.ByType(BTO);
        if (!string.IsNullOrWhiteSpace(tj)) text = MpaiJson.FromJson<BasicTextObject>(tj)?.GetText() ?? "";

        Diag("turn: " + (typedText is null ? "speak" : "typed") + " " + from + "->" + to + " wavBytes=" + wav.Length + " text=" + text);
        return (wav, fdo, text);
    }

    private async Task<BasicSpeechObject?> CaptureSpeechAsync(string sourceLanguage)
    {
        try
        {
            var wav = await Task.Run(() => _avatar!.CaptureSpeech()?.Data);
            if (wav is not { Length: > 0 }) return null;
            // Stamp the captured speech with its SOURCE language so ASR (Whisper)
            // decodes it as that language, not auto-detect / the static default.
            var qualifier = new SpeechQualifier
            {
                SpeechQualifierID = Guid.NewGuid().ToString(),
                Attributes = new SpeechAttributes
                {
                    Metadata = new SpeechMetadata
                    {
                        Language = new Language { LanguageCode = sourceLanguage, LanguageFormat = LanguageFormat.Iso639_1 }
                    }
                }
            };
            return BasicSpeechObject.FromData(wav, qualifier);
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

    // One-shot spoken prompt via PAF-RSR (welcome / instructions).
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

    protected override void OnClosed(EventArgs e)
    {
        try { if (_matStarted) { _north?.StopFlow(MatModule); _matStarted = false; } } catch { }
        base.OnClosed(e);
    }

    private void SetStatus(string s) => Dispatcher.Invoke(() => StatusText.Text = s);
    private void SetTurn(string s)   => Dispatcher.Invoke(() => TurnStatus.Text = s);
}