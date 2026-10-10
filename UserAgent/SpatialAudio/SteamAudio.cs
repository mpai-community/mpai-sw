using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Mpai.SpatialAudio;

// STEAM AUDIO (Valve, Apache-2.0), version 4.8.1: the few functions of its C API
// (phonon.h) that the User Agent's spatial renderer calls, declared as the header
// declares them. The native library - phonon.dll on Windows, libphonon.so on
// Linux - is not in the repository: it is obtained with Steam Audio's SDK and put
// in Models/SteamAudio/<windows-x64 | linux-x64>, as the models are
// (THIRD-PARTY-NOTICES.md). SteamAudio.Load names where it is.
internal static class SteamAudio
{
    private const string Lib = "phonon";
    public const uint Version = (4u << 16) | (8u << 8) | 1u;

    private static string? _folder;

    // Where the native library is; once, before the first call.
    public static void Load(string folder)
    {
        if (_folder is not null) return;
        _folder = folder;
        NativeLibrary.SetDllImportResolver(typeof(SteamAudio).Assembly, (name, assembly, path) =>
        {
            if (name != Lib) return IntPtr.Zero;
            var file = Path.Combine(_folder, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows-x64/phonon.dll" : "linux-x64/libphonon.so");
            return File.Exists(file) ? NativeLibrary.Load(file) : IntPtr.Zero;
        });
    }

    public static bool Present(string folder) =>
        File.Exists(Path.Combine(folder, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows-x64/phonon.dll" : "linux-x64/libphonon.so"));

    [StructLayout(LayoutKind.Sequential)] public struct Vector3 { public float X, Y, Z; public Vector3(float x, float y, float z) { X = x; Y = y; Z = z; } }

    [StructLayout(LayoutKind.Sequential)] public struct ContextSettings { public uint Version; public IntPtr Log, Allocate, Free; public int SimdLevel; public int Flags; }
    [StructLayout(LayoutKind.Sequential)] public struct AudioSettings { public int SamplingRate; public int FrameSize; }
    [StructLayout(LayoutKind.Sequential)] public struct HrtfSettings { public int Type; public IntPtr SofaFileName; public IntPtr SofaData; public int SofaDataSize; public float Volume; public int NormType; }
    [StructLayout(LayoutKind.Sequential)] public struct BinauralEffectSettings { public IntPtr Hrtf; }
    [StructLayout(LayoutKind.Sequential)] public struct BinauralEffectParams { public Vector3 Direction; public int Interpolation; public float SpatialBlend; public IntPtr Hrtf; public IntPtr PeakDelays; }
    [StructLayout(LayoutKind.Sequential)] public struct SpeakerLayout { public int Type; public int NumSpeakers; public IntPtr Speakers; }
    [StructLayout(LayoutKind.Sequential)] public struct PanningEffectSettings { public SpeakerLayout SpeakerLayout; }
    [StructLayout(LayoutKind.Sequential)] public struct PanningEffectParams { public Vector3 Direction; }
    [StructLayout(LayoutKind.Sequential)] public struct DirectEffectSettings { public int NumChannels; }
    [StructLayout(LayoutKind.Sequential)] public struct DirectEffectParams
    {
        public int Flags; public int TransmissionType; public float DistanceAttenuation;
        public float Air0, Air1, Air2; public float Directivity; public float Occlusion; public float Transmission0, Transmission1, Transmission2;
    }
    [StructLayout(LayoutKind.Sequential)] public struct AudioBuffer { public int NumChannels; public int NumSamples; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] public struct DistanceAttenuationModel { public int Type; public float MinDistance; public IntPtr Callback; public IntPtr UserData; public int Dirty; }
    [StructLayout(LayoutKind.Sequential)] public struct AirAbsorptionModel { public int Type; public float C0, C1, C2; public IntPtr Callback; public IntPtr UserData; public int Dirty; }

    public const int DirectDistanceAttenuation = 1 << 0, DirectAirAbsorption = 1 << 1, DirectDirectivity = 1 << 2;
    public const int SpeakerMono = 0, SpeakerStereo = 1, SpeakerQuad = 2, Speaker51 = 3, Speaker71 = 4;

    [DllImport(Lib)] public static extern int iplContextCreate(ref ContextSettings settings, out IntPtr context);
    [DllImport(Lib)] public static extern void iplContextRelease(ref IntPtr context);
    [DllImport(Lib)] public static extern int iplHRTFCreate(IntPtr context, ref AudioSettings audio, ref HrtfSettings settings, out IntPtr hrtf);
    [DllImport(Lib)] public static extern void iplHRTFRelease(ref IntPtr hrtf);
    [DllImport(Lib)] public static extern int iplBinauralEffectCreate(IntPtr context, ref AudioSettings audio, ref BinauralEffectSettings settings, out IntPtr effect);
    [DllImport(Lib)] public static extern int iplBinauralEffectApply(IntPtr effect, ref BinauralEffectParams p, ref AudioBuffer input, ref AudioBuffer output);
    [DllImport(Lib)] public static extern void iplBinauralEffectRelease(ref IntPtr effect);
    [DllImport(Lib)] public static extern int iplPanningEffectCreate(IntPtr context, ref AudioSettings audio, ref PanningEffectSettings settings, out IntPtr effect);
    [DllImport(Lib)] public static extern int iplPanningEffectApply(IntPtr effect, ref PanningEffectParams p, ref AudioBuffer input, ref AudioBuffer output);
    [DllImport(Lib)] public static extern void iplPanningEffectRelease(ref IntPtr effect);
    [DllImport(Lib)] public static extern int iplDirectEffectCreate(IntPtr context, ref AudioSettings audio, ref DirectEffectSettings settings, out IntPtr effect);
    [DllImport(Lib)] public static extern int iplDirectEffectApply(IntPtr effect, ref DirectEffectParams p, ref AudioBuffer input, ref AudioBuffer output);
    [DllImport(Lib)] public static extern void iplDirectEffectRelease(ref IntPtr effect);
    [DllImport(Lib)] public static extern int iplAudioBufferAllocate(IntPtr context, int numChannels, int numSamples, ref AudioBuffer buffer);
    [DllImport(Lib)] public static extern void iplAudioBufferFree(IntPtr context, ref AudioBuffer buffer);
    [DllImport(Lib)] public static extern void iplAudioBufferInterleave(IntPtr context, ref AudioBuffer src, float[] dst);
    [DllImport(Lib)] public static extern void iplAudioBufferDeinterleave(IntPtr context, float[] src, ref AudioBuffer dst);
    [DllImport(Lib)] public static extern float iplDistanceAttenuationCalculate(IntPtr context, Vector3 source, Vector3 listener, ref DistanceAttenuationModel model);
    [DllImport(Lib)] public static extern void iplAirAbsorptionCalculate(IntPtr context, Vector3 source, Vector3 listener, ref AirAbsorptionModel model, float[] airAbsorption);
    [DllImport(Lib)] public static extern Vector3 iplCalculateRelativeDirection(IntPtr context, Vector3 sourcePosition, Vector3 listenerPosition, Vector3 listenerAhead, Vector3 listenerUp);
}
