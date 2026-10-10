using System;
using System.Collections.Concurrent;
using System.IO;

using Microsoft.ML.OnnxRuntime;

namespace Mpai.Onnx;

// EVERY ONNX MODEL OF AN AIM IS OPENED HERE (the author, 2026/10/06: a server with a
// 16 GB GPU answered more slowly than a desktop with a 4 GB one - nothing but the
// language model used the GPU). A session is opened on the GPU where it can be and on
// the CPU otherwise, and the choice is said once per model:
//
//   MPAI_ONNX_DEVICE = auto  (default) CUDA if this build and this machine have it, else CPU
//                      cuda  CUDA or fail - for a machine that must not run slowly unnoticed
//                      cpu   the CPU, whatever the build
//
// A build has CUDA only with -p:MpaiOnnxGpu=true (Mpai.Onnx.csproj). Operators CUDA does
// not implement run on the CPU within the same session; ONNX Runtime places them itself.
public static class OnnxSessions
{
    private static readonly string Device =
        (Environment.GetEnvironmentVariable("MPAI_ONNX_DEVICE") ?? "auto").Trim().ToLowerInvariant();

    // Once CUDA has been found absent, it is not tried for every further model.
    private static volatile bool cudaAbsent;
    private static readonly ConcurrentDictionary<string, string> said = new(StringComparer.Ordinal);

    public static InferenceSession Create(string modelPath)
    {
        if (Device != "cpu" && !cudaAbsent)
        {
            SessionOptions? options = null;
            try
            {
                options = new SessionOptions();
                options.AppendExecutionProvider_CUDA(0);
                var session = new InferenceSession(modelPath, options);
                Say(modelPath, "CUDA");
                return session;
            }
            catch (Exception e) when (Device != "cuda")
            {
                options?.Dispose();
                cudaAbsent = true;
                Say(modelPath, "CPU (CUDA unavailable: " + e.GetBaseException().Message.Split('\n')[0].Trim() + ")");
            }
        }
        else Say(modelPath, "CPU");
        return new InferenceSession(modelPath);
    }

    private static void Say(string modelPath, string device)
    {
        if (said.TryAdd(modelPath, device))
            Console.WriteLine($"[ONNX] {Path.GetFileName(modelPath)}: {device}");
    }
}
