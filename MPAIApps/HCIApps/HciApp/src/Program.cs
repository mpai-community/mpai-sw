using System;
using System.IO;
namespace HciApp;
internal static class Program
{
    public static readonly string CrashLog =
        Path.Combine(AppContext.BaseDirectory, "hci-crash.log");
    public static void Record(string context, Exception error)
    {
        try { File.AppendAllText(CrashLog, $"{DateTime.Now:o}  [{context}]  {error}{Environment.NewLine}"); }
        catch { }
    }
}
