using System;
using System.IO;
namespace HciMac;
// Crash-log helper for CAV-MAC. The WPF entry point is generated from App.xaml
// (StartupUri), so this type no longer defines Main - it just provides the crash
// log that MainWindow writes to on a startup failure.
internal static class Program
{
    public static readonly string CrashLog =
        Path.Combine(AppContext.BaseDirectory, "cav-mac-crash.log");

    public static void Record(string context, Exception error)
    {
        try { File.AppendAllText(CrashLog, $"{DateTime.Now:o}  [{context}]  {error}{Environment.NewLine}"); }
        catch { }
    }
}
