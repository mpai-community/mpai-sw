using System;
using System.IO;
namespace AcrApp;
// Crash-log helper for HCI-ACR. The WPF entry point is generated from App.xaml
// (StartupUri); this type just provides the crash log MainWindow writes to on a
// startup failure.
internal static class Program
{
    public static readonly string CrashLog =
        Path.Combine(AppContext.BaseDirectory, "acr-crash.log");

    public static void Record(string context, Exception error)
    {
        try { File.AppendAllText(CrashLog, $"{DateTime.Now:o}  [{context}]  {error}{Environment.NewLine}"); }
        catch { }
    }
}
