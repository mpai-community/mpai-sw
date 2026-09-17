using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Mpai.Core;
using Mpai.Mas.Client;
using Mpai.Mas.PortData;
using Mpai.Rca;
using Mpai.Wdl;

namespace MpaiRca.Console;

// A REMOTE CLIENT APPLICATION WITH NO FACE, AND NO APPLICATION EITHER.
//
// It is given a workflow description and the address of a Service. It holds the
// devices - here, stand-ins that read the keyboard and write the screen - and the
// MPAI-MAS client, and it hands both to the interpreter. Nothing in it names an
// application: which Modules run, which Ports they take and which they give, all
// come from the document.
//
// The devices are stand-ins because a console has no camera and no avatar. They
// are enough to prove that the interpreter drives a real Module correctly; the
// windowed client replaces them with a microphone, a camera and the lady.
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 1)
        {
            Con.WriteLine("RcaConsole <workflow.orch> [service-url]");
            Con.WriteLine("  service: MPAI_MAS_SERVER, or the second argument.");
            return 2;
        }

        var path = args[0];
        var url  = args.Length > 1 ? args[1]
                 : Environment.GetEnvironmentVariable("MPAI_MAS_SERVER")
                 ?? "https://localhost:5006/";

        if (!File.Exists(path)) { Con.WriteLine($"no such workflow: {path}"); return 2; }

        Workflow workflow;
        try { workflow = new WorkflowReader().Read(File.ReadAllText(path)); }
        catch (Exception ex) { Con.WriteLine("the workflow could not be read - " + ex.Message); return 1; }

        Con.WriteLine($"=== {Path.GetFileName(path)}");
        Con.WriteLine($"    workflow {workflow.Name} over {string.Join(", ", workflow.Modules)}");
        Con.WriteLine($"    service  {url}");
        Con.WriteLine();

        using var north = new RemoteNorthApi(
            url, Environment.GetEnvironmentVariable("MPAI_MAS_TOKEN"));

        var devices = StandIns();
        var stop    = new CancellationTokenSource();

        Con.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); Con.WriteLine("\n(stopping)"); };

        var interpreter = new WorkflowInterpreter(north, devices, Con.WriteLine);
        try
        {
            await interpreter.RunAsync(workflow, stop.Token);
            return 0;
        }
        catch (Exception ex)
        {
            Con.WriteLine("stopped: " + ex.Message);
            return 1;
        }
    }

    // ---- the stand-in devices ----------------------------------------------

    private static DeviceRegistry StandIns()
    {
        var devices = new DeviceRegistry();

        // Text is typed. This is the one device a console genuinely has.
        devices.RegisterAcquire("OSD-BTO-V1.5", _ =>
        {
            Con.Write("  > ");
            var line = Con.ReadLine() ?? "";
            return Task.FromResult<string?>(MpaiJson.ToJson(BasicTextObject.FromText(line)));
        });

        // A time is asked of the clock, which a console has as surely as any
        // machine does. The only stand-in here is that nothing was captured.
        devices.RegisterAcquire("OSD-STM-V1.5", _ =>
            Task.FromResult<string?>(MpaiJson.ToJson(Now())));

        // Speech and vision a console has not. Rather than invent an Object with
        // no Qualifier - the fault of every User Agent before this week - it
        // declines and says which device is missing.
        devices.RegisterAcquire("OSD-BSO-V1.5", _ =>
        {
            Con.WriteLine("  (no microphone in a console client)");
            return Task.FromResult<string?>(null);
        });
        devices.RegisterAcquire("OSD-BVO-V1.5", _ =>
        {
            Con.WriteLine("  (no camera in a console client)");
            return Task.FromResult<string?>(null);
        });

        // Presenting: the screen takes what it can read and reports the rest by
        // its size, since a console cannot play speech or animate a face.
        devices.RegisterPresent("screen", data =>
        {
            foreach (var kv in data)
            {
                if (kv.Key == "Prompt") { Con.WriteLine("  " + kv.Value); continue; }

                if (kv.Key.StartsWith("OSD-BTO", StringComparison.OrdinalIgnoreCase) ||
                    kv.Key.StartsWith("Display:OSD-BTO", StringComparison.OrdinalIgnoreCase))
                {
                    Con.WriteLine("  " + Words(kv.Value));
                    continue;
                }
                if (kv.Key.StartsWith("Display", StringComparison.OrdinalIgnoreCase))
                {
                    Con.WriteLine("  " + kv.Value);
                    continue;
                }
                Con.WriteLine($"  ({kv.Key}, {kv.Value.Length:N0} characters - not rendered here)");
            }
            return Task.CompletedTask;
        });

        return devices;
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

    private static string Words(string json)
    {
        try { return MpaiJson.FromJson<BasicTextObject>(json)?.GetText() ?? ""; }
        catch { return "(unreadable)"; }
    }

    private static class Con
    {
        public static void WriteLine(string s = "") => System.Console.WriteLine(s);
        public static void Write(string s)          => System.Console.Write(s);
        public static string? ReadLine()            => System.Console.ReadLine();
        public static event ConsoleCancelEventHandler CancelKeyPress
        {
            add    => System.Console.CancelKeyPress += value;
            remove => System.Console.CancelKeyPress -= value;
        }
    }
}