using System;
using System.Linq;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Mpai.Mmc.Sir;

// Loads the 3D-Speaker ecapa-tdnn.onnx (input feature:[1,T,80], output
// embedding:[1,192]) and turns audio into an L2-normalised 192-d speaker
// embedding. Cosine similarity of two embeddings measures same-speaker-ness.
public sealed class SpeakerEmbedder : IDisposable
{
    private readonly InferenceSession _session;
    private readonly MelSpectrogram _fbank = new();
    private readonly string _inputName;
    private readonly int _frames;   // fixed T the model wants (360)

    public SpeakerEmbedder(string modelPath, int frames = 360)
    {
        _session = new InferenceSession(modelPath);
        _inputName = _session.InputMetadata.Keys.First();
        _frames = frames;
    }

    public float[] Embed(float[] samples)
    {
        var feats = _fbank.Compute(samples);            // [T][80]
        feats = MelSpectrogram.FixFrames(feats, _frames); // [360][80]

        int t = feats.Length, d = feats[0].Length;      // 360, 80
        var tensor = new DenseTensor<float>(new[] { 1, t, d });
        for (int i = 0; i < t; i++)
            for (int j = 0; j < d; j++)
                tensor[0, i, j] = feats[i][j];

        var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };
        using var results = _session.Run(inputs);
        var emb = results.First().AsFloatArray();

        // L2 normalise.
        double norm = 0.0;
        foreach (var v in emb) norm += v * (double)v;
        norm = Math.Sqrt(norm);
        var outp = new float[emb.Length];
        for (int i = 0; i < emb.Length; i++) outp[i] = (float)(emb[i] / norm);
        return outp;
    }

    public static double Cosine(float[] a, float[] b)
    {
        double dot = 0.0;
        for (int i = 0; i < a.Length; i++) dot += a[i] * (double)b[i];
        return dot; // both already L2-normalised
    }

    public void Dispose() => _session.Dispose();
}

internal static class TensorExt
{
    // Small helper to flatten the output tensor to float[].
    public static float[] AsFloatArray(this DisposableNamedOnnxValue v)
        => v.AsTensor<float>().ToArray();
}
