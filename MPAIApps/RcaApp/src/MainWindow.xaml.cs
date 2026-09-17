using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

using Microsoft.Win32;

using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Aims.Visual;
using Mpai.Hci.Api;
using Mpai.Mas.Client;
using Mpai.Rca;
using Mpai.UaKit;
using Mpai.Wdl;

namespace MpaiRca;

// A REMOTE CLIENT APPLICATION WITH A FACE, AND NO APPLICATION.
//
// It holds the real-world edges - a microphone, a webcam, a screen and the avatar
// - and the MPAI-MAS client, and hands both to the interpreter. Which Modules
// run, which Ports they take and which they give, all come from the Workflow
// Description it is opened with. Nothing here names an application, and nothing
// here holds a model: everything that reasons is on the Service.
//
// The device registry is where the notation meets the machine. A workflow says
// "acquire UserSpeech (OSD-BSO-V1.5)" and nothing about a microphone; the entry
// for OSD-BSO is what knows. Adding a Data Type to what this client can handle is
// adding an entry, not changing a program.
public partial class MainWindow : Window
{
    private static readonly string AmdDir    = MpaiPaths.Amds;
    private static readonly string AssetsDir = MpaiPaths.Assets;

    private AvatarUaHost?         _avatar;
    private Workflow?             _workflow;
    private string?               _workflowPath;
    private CancellationTokenSource? _stopping;

    // What the workflow is waiting for the user to type, if anything.
    private TaskCompletionSource<string>? _typed;

    public MainWindow()
    {
        InitializeComponent();
        Loaded      += OnLoaded;
        LoadButton.Click  += (_, _) => ChooseWorkflow();
        StartButton.Click += (_, _) => _ = RunAsync();
        StopButton.Click  += (_, _) => _stopping?.Cancel();
        SendButton.Click  += (_, _) => SendTyped();
        TypedBox.KeyDown  += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) SendTyped(); };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Status("starting the avatar...");
            _avatar = new AvatarUaHost(Web, Dispatcher, AmdDir, AssetsDir);
            await _avatar.InitAsync();
            await Task.Delay(TimeSpan.FromSeconds(1.5));

            // A workflow may be named on the command line, so that a shipped
            // client can be started on the one it is meant to run.
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && File.Exists(args[1])) Load(args[1]);

            Status("ready");
        }
        catch (Exception fatal)
        {
            Program.Record("startup", fatal);
            Status("startup failed: " + fatal.Message);
        }
    }

    // ---- the workflow ------------------------------------------------------

    private void ChooseWorkflow()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Workflow Description",
            Filter = "Workflow Description (*.orch)|*.orch|All files (*.*)|*.*",
            InitialDirectory = Path.Combine(MpaiPaths.Root, "UAs", "Orchestration")
        };
        if (dialog.ShowDialog() == true) Load(dialog.FileName);
    }

    private void Load(string path)
    {
        try
        {
            _workflow     = new WorkflowReader().Read(File.ReadAllText(path));
            _workflowPath = path;
            WorkflowText.Text =
                $"{Path.GetFileName(path)}   \u2014   workflow {_workflow.Name} over {string.Join(", ", _workflow.Modules)}";
            InstructionText.Text = "Press Start.";
            StartButton.IsEnabled = true;
            Status("workflow read");
        }
        catch (Exception ex)
        {
            // A workflow that will not read fails here, where it is being read,
            // and not later where the consequence would show.
            WorkflowText.Text = Path.GetFileName(path) + " \u2014 " + ex.Message;
            StartButton.IsEnabled = false;
            Status("the workflow could not be read");
        }
    }

    private async Task RunAsync()
    {
        if (_workflow is null) return;

        var url = Environment.GetEnvironmentVariable("MPAI_MAS_SERVER")
               ?? "https://localhost:5006/";

        StartButton.IsEnabled = false;
        StopButton.IsEnabled  = true;
        _stopping = new CancellationTokenSource();

        try
        {
            using var north = new RemoteNorthApi(
                url, Environment.GetEnvironmentVariable("MPAI_MAS_TOKEN"));

            var interpreter = new WorkflowInterpreter(north, Devices(), Status);
            await interpreter.RunAsync(_workflow, _stopping.Token);
            Status("the workflow finished");
        }
        catch (Exception ex)
        {
            Program.Record("workflow", ex);
            Status("stopped: " + ex.Message);
        }
        finally
        {
            StopButton.IsEnabled  = false;
            StartButton.IsEnabled = true;
            TypedBox.IsEnabled = SendButton.IsEnabled = false;
        }
    }
    // ---- the devices -------------------------------------------------------

    private DeviceRegistry Devices()
    {
        var devices = new DeviceRegistry();

        // SPEECH, FROM THE MICROPHONE. The capture returns the Object that Speech
        // Object Acquisition built, Qualifier and all: the sampling frequency and
        // the precision the device determined. Taking the bytes out and rebuilding
        // is what four User Agents did until this week, and it is why the voice
        // half of every enrolment failed in silence.
        devices.RegisterAcquire("OSD-BSO-V1.5", async viaVad =>
        {
            Instruct("Speak when you are ready.");
            var speech = await Task.Run(() => _avatar!.CaptureSpeech());
            return speech is null || speech.Data.Length == 0
                ? null
                : MpaiJson.ToJson(speech);
        });

        // A FACE, FROM THE CAMERA.
        devices.RegisterAcquire("OSD-BVO-V1.5", async _ =>
        {
            Instruct("Look at the camera.");
            var frame = await Task.Run(() =>
                new WebcamVisualAcquisition()
                    .AcquireAsync(new VisualAcquisitionRequest { VisualObjectType = "Face" })
                    .GetAwaiter().GetResult().Data);

            return frame is { Length: > 0 }
                ? MpaiJson.ToJson(BasicVisualObject.FromFile("webcam.jpg", frame, "Face"))
                : null;
        });

        // WORDS, FROM THE KEYBOARD. The window opens its text box and waits.
        devices.RegisterAcquire("OSD-BTO-V1.5", async _ =>
        {
            var words = await TypedAsync();
            return string.IsNullOrWhiteSpace(words)
                ? null
                : MpaiJson.ToJson(BasicTextObject.FromText(words));
        });

        // A TIME, FROM THE CLOCK.
        devices.RegisterAcquire("OSD-STM-V1.5", _ =>
            Task.FromResult<string?>(MpaiJson.ToJson(Now())));

        // THE AVATAR. Speech and face descriptors are one utterance, not two, so a
        // presenter is offered everything being presented and takes what it knows.
        devices.RegisterPresent("avatar", async data =>
        {
            byte[] wav = Array.Empty<byte>();
            FaceDescriptorsObject? fdo = null;

            if (data.TryGetValue("OSD-BSO-V1.5", out var sj) && !string.IsNullOrWhiteSpace(sj))
                wav = MpaiJson.FromJson<BasicSpeechObject>(sj)?.Data ?? Array.Empty<byte>();

            if (data.TryGetValue("PAF-FDO-V1.6", out var fj) && !string.IsNullOrWhiteSpace(fj))
                fdo = MpaiJson.FromJson<FaceDescriptorsObject>(fj);

            if (wav.Length == 0 && fdo is null) return;

            await _avatar!.PresentAsync(new SpeakingAvatar(wav, fdo, null));
            await Task.Delay(TimeSpan.FromSeconds(AvatarUaHost.WavDurationSeconds(wav) + 0.3));
        });

        // THE SCREEN.
        devices.RegisterPresent("screen", data =>
        {
            foreach (var kv in data)
            {
                if (kv.Key == "Prompt") { Instruct(kv.Value); continue; }

                if (kv.Key.StartsWith("Display:OSD-BTO", StringComparison.OrdinalIgnoreCase) ||
                    kv.Key.Equals("OSD-BTO-V1.5", StringComparison.OrdinalIgnoreCase))
                { Instruct(Words(kv.Value)); continue; }

                if (kv.Key.StartsWith("Display", StringComparison.OrdinalIgnoreCase))
                    Instruct(kv.Value);
            }
            return Task.CompletedTask;
        });

        return devices;
    }

    // ---- the window --------------------------------------------------------

    private Task<string> TypedAsync()
    {
        _typed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.Invoke(() =>
        {
            TypedBox.IsEnabled = SendButton.IsEnabled = true;
            TypedBox.Clear();
            TypedBox.Focus();
        });
        return _typed.Task;
    }

    private void SendTyped()
    {
        if (_typed is null) return;
        var words = TypedBox.Text;
        TypedBox.IsEnabled = SendButton.IsEnabled = false;
        var waiting = _typed;
        _typed = null;
        waiting.TrySetResult(words);
    }

    private void Instruct(string text) =>
        Dispatcher.Invoke(() => InstructionText.Text = text);

    private void Status(string text) =>
        Dispatcher.Invoke(() => StatusText.Text = text);

    private static string Words(string json)
    {
        try { return MpaiJson.FromJson<BasicTextObject>(json)?.GetText() ?? ""; }
        catch { return ""; }
    }

    private static SimpleTime Now()
    {
        var t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        return new SimpleTime
        {
            SimpleTimeID   = Guid.NewGuid().ToString(),
            SimpleTimeData = new List<TimeSegment>
            {
                new TimeSegment { FlagsByte = 0, StartTime = t, EndTime = t, AccuracyMode = "single" }
            }
        };
    }
}