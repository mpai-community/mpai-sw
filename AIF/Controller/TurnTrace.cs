namespace AIF.Controller;

// THE TIMES OF A TURN (the author, 2026/09/30: Thalia answers late; where do the
// seconds go?). Off unless diagnostics are switched on - MPAI_DIAG=1 before the
// program starts, as for Mpai.Core.MpaiDiag - and then one line per event, in
// <temporary folder>\mpai-diag\timing.log: when, and how long. Never throws.
public static class TurnTrace
{
    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("MPAI_DIAG") == "1";

    private static readonly string File = Path.Combine(Path.GetTempPath(), "mpai-diag", "timing.log");
    private static readonly object Gate = new();

    public static void Line(string what)
    {
        if (!Enabled) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(File)!);
                System.IO.File.AppendAllText(File, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + what + Environment.NewLine);
            }
        }
        catch { }
    }
}
