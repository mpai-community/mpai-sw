using System;
using System.IO;
namespace MmcAmq;
internal static class Program
{
    public static readonly string CrashLog =
        Path.Combine(AppContext.BaseDirectory, "amq-crash.log");
    public static void Record(string context, Exception error)
    {
        try { File.AppendAllText(CrashLog, $"{DateTime.Now:o}  [{context}]  {error}{Environment.NewLine}"); }
        catch { }
    }
}
