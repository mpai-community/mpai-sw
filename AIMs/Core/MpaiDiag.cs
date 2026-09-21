using System;
using System.IO;

namespace Mpai.Core;

// DIAGNOSTICS: OFF UNLESS ASKED FOR, AND NEVER ANYWHERE BUT THE TEMPORARY FOLDER.
//
// What people say and show - their voice, their words, their face - is theirs.
// Nothing here writes any of it unless diagnostics are switched on, by setting the
// environment variable MPAI_DIAG=1 before starting a program; and when they are on,
// everything goes to one place, <temporary folder>\mpai-diag, on the machine that
// runs the program. No diagnostic names a folder of its own.
//
// Every AIM that wants to leave a trace calls this, and nothing else. Never throws.
public static class MpaiDiag
{
    public static bool Enabled { get; } =
        Environment.GetEnvironmentVariable("MPAI_DIAG") == "1";

    public static string Dir { get; } = Path.Combine(Path.GetTempPath(), "mpai-diag");

    // Text appended to a named log in the diagnostic folder.
    public static void Append(string file, string text)
    {
        if (!Enabled) return;
        try { Directory.CreateDirectory(Dir); File.AppendAllText(Path.Combine(Dir, Path.GetFileName(file)), text); }
        catch { }
    }

    // A named text or binary file in the diagnostic folder, replaced each time.
    public static void WriteText(string file, string text)
    {
        if (!Enabled) return;
        try { Directory.CreateDirectory(Dir); File.WriteAllText(Path.Combine(Dir, Path.GetFileName(file)), text); }
        catch { }
    }

    public static void WriteBytes(string file, byte[]? data)
    {
        if (!Enabled || data is null) return;
        try { Directory.CreateDirectory(Dir); File.WriteAllBytes(Path.Combine(Dir, Path.GetFileName(file)), data); }
        catch { }
    }

    // Raw 16-bit mono PCM as a playable WAV, tagged by a stage label.
    public static void DumpPcm(byte[]? pcm, int rate, string label)
    {
        if (!Enabled) return;
        try
        {
            if (pcm is null || pcm.Length == 0) { Log($"{label} EMPTY"); return; }
            if (rate <= 0) rate = 16000;
            Directory.CreateDirectory(Dir);
            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms))
            {
                int byteRate = rate * 2, dataLen = pcm.Length;
                bw.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' }); bw.Write(36 + dataLen);
                bw.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
                bw.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' }); bw.Write(16);
                bw.Write((short)1); bw.Write((short)1); bw.Write(rate); bw.Write(byteRate);
                bw.Write((short)2); bw.Write((short)16);
                bw.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' }); bw.Write(dataLen); bw.Write(pcm);
            }
            File.WriteAllBytes(Path.Combine(Dir, label + ".wav"), ms.ToArray());
            Log($"{label} len={pcm.Length} rate={rate}");
        }
        catch (Exception e) { try { Log($"{label} ERR {e.Message}"); } catch { } }
    }

    // Base64-encoded raw PCM.
    public static void DumpB64(string? base64, int rate, string label)
    {
        if (!Enabled) return;
        try { DumpPcm(string.IsNullOrEmpty(base64) ? null : Convert.FromBase64String(base64), rate, label); }
        catch (Exception e) { try { Log($"{label} B64ERR {e.Message}"); } catch { } }
    }

    // Arbitrary text, e.g. descriptors JSON.
    public static void DumpText(string? text, string label)
    {
        if (!Enabled) return;
        try { Directory.CreateDirectory(Dir); File.WriteAllText(Path.Combine(Dir, label + ".json"), text ?? "(null)"); Log($"{label} textLen={(text?.Length ?? -1)}"); }
        catch { }
    }

    private static void Log(string line) =>
        Append("flow.log", DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + Environment.NewLine);
}
