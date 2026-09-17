using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Threading.Tasks;

namespace HciMat;

public partial class App : Application
{
    // Beside the executable, not on one author's disk: a path that exists only
    // on the machine it was written on is a log nobody else can read.
    private static readonly string CrashLog = Program.CrashLog;

    public static void Log(string context, object error)
    {
        try { File.AppendAllText(CrashLog, $"{DateTime.Now:o}  [{context}]  {error}{Environment.NewLine}"); } catch { }
    }

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("AppDomain", e.ExceptionObject);
        DispatcherUnhandledException += (s, e) => { Log("Dispatcher", e.Exception); e.Handled = true; };
        TaskScheduler.UnobservedTaskException += (s, e) => { Log("Task", e.Exception); e.SetObserved(); };

        // AN AIM IS SILENT UNTIL A HOST ASKS TO HEAR IT. AimLog installs no sink by
        // default, so every warning an AIM was written to give goes nowhere unless
        // the application asks for it.
        Mpai.Core.AimLog.Sink = (aim, message) => Log(aim, message);
    }
}
