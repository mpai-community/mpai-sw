using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using AIF.Controller;
using AIF.Store;

using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Hci.Api;   // NorthApi

namespace HciSceneTest;

// Stand-alone bring-up test for the MMC-HCI reference Module. Feeds ONE turn of
// Audio (OSD-BAO from a WAV), Visual (OSD-BVO from a JPG) and a synthetic LiDAR
// object (OSD-BLO) through Advance(MMC-HCI, ...) and reports how far the 18-AIM
// scene chain gets: which outputs were produced, or which AIM failed.
internal static class Program
{
    private const string HciModule = "MMC-HCI-V2.5";

    private const string BAO = "OSD-BAO-V1.5";   // audio input
    private const string BVO = "OSD-BVO-V1.5";   // visual (face) input
    private const string BLO = "OSD-BLO-V1.5";   // lidar input
    private const string IID = "OSD-IID-V1.5";   // OutputUserID
    private const string BSO = "OSD-BSO-V1.5";   // OutputSpeech
    private const string BTO = "OSD-BTO-V1.5";   // OutputText

    private static readonly string AmdDir       = Mpai.Core.MpaiPaths.Amds;
    private static readonly string SettingsPath = Mpai.Core.MpaiPaths.Settings;
    private static readonly string GalleryJson  = Mpai.Core.MpaiPaths.Gallery;

    [STAThread]
    private static void Main(string[] args)
    {
        string wavPath = args.Length > 0 ? args[0] : @"D:\AI\MPAIApps\AudioIn\question-spoken.wav";
        string jpgPath = args.Length > 1 ? args[1] : @"D:\AI\TestData\Images\leonardo.jpg";

        Console.WriteLine("=== HCI scene-chain bring-up test ===");
        Console.WriteLine($"AmdDir   : {AmdDir}");
        Console.WriteLine($"WAV      : {wavPath}  (exists={File.Exists(wavPath)})");
        Console.WriteLine($"JPG      : {jpgPath}  (exists={File.Exists(jpgPath)})");
        Console.WriteLine();

        NorthApi? north = null;
        try
        {
            north = new NorthApi(AmdDir, SettingsPath, store => new HciApp.HciProvider(store, GalleryJson));
            Console.WriteLine("NorthApi + HciProvider built OK.");

            var inputs = new List<NorthApi.Datum>();

            // --- Audio (OSD-BAO) from the WAV ---
            var bao = BuildAudioObject(File.ReadAllBytes(wavPath));
            inputs.Add(new NorthApi.Datum(BAO, MpaiJson.ToJson(bao)));
            Console.WriteLine("built OSD-BAO from WAV.");

            // --- Visual (OSD-BVO face) from the JPG ---
            var bvo = BasicVisualObject.FromFile(Path.GetFileName(jpgPath), File.ReadAllBytes(jpgPath), "Face");
            inputs.Add(new NorthApi.Datum(BVO, MpaiJson.ToJson(bvo)));
            Console.WriteLine("built OSD-BVO from JPG.");

            // --- LiDAR (OSD-BLO) synthetic minimal ---
            var blo = new BasicLiDARObject
            {
                BasicLiDARObjectID = Guid.NewGuid().ToString("N"),
                BasicLiDARData = new List<object>()
            };
            inputs.Add(new NorthApi.Datum(BLO, MpaiJson.ToJson(blo)));
            Console.WriteLine("built synthetic OSD-BLO.");
            Console.WriteLine();

            Console.WriteLine("StartFlow(MMC-HCI)...");
            var started = north.StartFlow(HciModule);
            Console.WriteLine($"  StartFlow -> {started}");
            if (started != AifError.OK) { Console.WriteLine("cannot start; aborting."); return; }

            Console.WriteLine("Advance(MMC-HCI, [BAO, BVO, BLO])...");
            var r = north.Advance(HciModule, inputs);
            Console.WriteLine($"  Advance -> Error={r.Error}  Suspended={r.Suspended}  Ok={r.Ok}");
            Console.WriteLine();

            Console.WriteLine("--- outputs ---");
            Dump("OutputUserID (OSD-IID)", r.ByType(IID));
            Dump("OutputSpeech (OSD-BSO)", r.ByType(BSO));
            Dump("OutputText   (OSD-BTO)", r.ByType(BTO));

            // decode the identity if present
            var iid = r.ByType(IID);
            if (!string.IsNullOrWhiteSpace(iid))
            {
                Console.WriteLine();
                Console.WriteLine($"IDENTITY JSON: {Trunc(iid, 400)}");
            }

            north.StopFlow(HciModule);
            Console.WriteLine();
            Console.WriteLine("=== done ===");
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("!!! EXCEPTION !!!");
            Console.WriteLine(ex.ToString());
        }
        finally
        {
            (north as IDisposable)?.Dispose();
        }
    }

    private static void Dump(string label, string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) Console.WriteLine($"  {label}: (empty)");
        else Console.WriteLine($"  {label}: {Trunc(json, 160)}");
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + " ...";

    // Build a valid OSD-BAO from a WAV: strip the 44-byte header, base64 the PCM,
    // and fill the qualifier QCV reads (SampleSpace freq+precision, channels).
    private static BasicAudioObject BuildAudioObject(byte[] wav)
    {
        int rate = 16000, bits = 16, channels = 1, dataOffset = 44, dataLen = wav.Length - 44;

        // Parse the WAV header if present ("RIFF"/"WAVE"); else assume 16k mono 16-bit raw.
        if (wav.Length > 44 && wav[0] == 0x52 && wav[1] == 0x49 && wav[2] == 0x46 && wav[3] == 0x46)
        {
            channels = wav[22] | (wav[23] << 8);
            rate     = wav[24] | (wav[25] << 8) | (wav[26] << 16) | (wav[27] << 24);
            bits     = wav[34] | (wav[35] << 8);
            // find the "data" chunk
            int i = 12;
            while (i + 8 <= wav.Length)
            {
                int id = wav[i] | (wav[i+1]<<8) | (wav[i+2]<<16) | (wav[i+3]<<24);
                int sz = wav[i+4] | (wav[i+5]<<8) | (wav[i+6]<<16) | (wav[i+7]<<24);
                if (wav[i]==0x64 && wav[i+1]==0x61 && wav[i+2]==0x74 && wav[i+3]==0x61) // "data"
                { dataOffset = i + 8; dataLen = sz; break; }
                i += 8 + sz;
            }
        }
        if (dataLen < 0 || dataOffset + dataLen > wav.Length) { dataOffset = 44; dataLen = wav.Length - 44; }
        var pcm = new byte[dataLen];
        Array.Copy(wav, dataOffset, pcm, 0, dataLen);

        return new BasicAudioObject
        {
            BasicAudioObjectID = Guid.NewGuid().ToString("N"),
            BasicAudioObjectData = new List<BasicAudioObjectDataItem> { new InlineAudioData(Convert.ToBase64String(pcm)) },
            AudioQualifier = new AudioQualifier
            {
                AudioQualifierID = Guid.NewGuid().ToString("N"),
                Formats = new AudioFormats
                {
                    ContentFormat = new AudioContentFormat
                    {
                        RawData = new AudioRawData
                        {
                            SampleSpace = new Pcm { SamplingFrequency = rate, Precision = bits }
                        }
                    }
                },
                Attributes = new AudioAttributes
                {
                    Device = new AudioDevice
                    {
                        DeviceRole = "Capture",
                        DeviceType = "Microphone",
                        CaptureConfiguration = new CaptureConfiguration { ChannelCount = channels }
                    }
                }
            }
        };
    }
}
