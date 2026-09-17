using System;
using System.IO;

namespace Mpai.Core;

// Diagnostic audio dump: writes a playable 16-bit mono WAV for any raw-PCM buffer,
// tagged by a stage label, to a fixed folder. One-line calls at each pipeline
// stage let us hear the audio content as it flows. Never throws.
public static class MpaiDiag
{
    public static string Dir = @"C:\Users\Leonardo\Downloads\hci-flow";

    // Dump raw 16-bit mono PCM as a playable WAV at the given rate.
    public static void DumpPcm(byte[]? pcm, int rate, string label)
    {
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

    // Dump base64-encoded raw PCM (common in inline audio data).
    public static void DumpB64(string? base64, int rate, string label)
    {
        try { DumpPcm(string.IsNullOrEmpty(base64) ? null : Convert.FromBase64String(base64), rate, label); }
        catch (Exception e) { try { Log($"{label} B64ERR {e.Message}"); } catch { } }
    }

    // Dump arbitrary text (e.g. descriptors JSON) for inspection.
    public static void DumpText(string? text, string label)
    {
        try { Directory.CreateDirectory(Dir); File.WriteAllText(Path.Combine(Dir, label + ".json"), text ?? "(null)"); Log($"{label} textLen={(text?.Length ?? -1)}"); }
        catch { }
    }

    private static void Log(string line)
    {
        try { Directory.CreateDirectory(Dir); File.AppendAllText(Path.Combine(Dir, "flow.log"), DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + Environment.NewLine); } catch { }
    }
}
