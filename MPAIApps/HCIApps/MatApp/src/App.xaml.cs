using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Threading.Tasks;

namespace HciMat;

public partial class App : Application
{
    private static readonly string CrashLog = @"C:\Users\Leonardo\Downloads\mat-crash.log";

    public static void Log(string context, object error)
    {
        try { File.AppendAllText(CrashLog, $"{DateTime.Now:o}  [{context}]  {error}{Environment.NewLine}"); } catch { }
    }

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("AppDomain", e.ExceptionObject);
        DispatcherUnhandledException += (s, e) => { Log("Dispatcher", e.Exception); e.Handled = true; };
        TaskScheduler.UnobservedTaskException += (s, e) => { Log("Task", e.Exception); e.SetObserved(); };
    }
}
