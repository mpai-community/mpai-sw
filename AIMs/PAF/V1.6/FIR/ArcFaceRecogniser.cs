using System;
using System.Linq;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Mpai.Paf.Fir;

// ---------------------------------------------------------------------------
//  ArcFace / AuraFace face RECOGNITION (embedding extraction).
//
//  This is the IDENTIFY half - downstream of the OSD visual scene's DESCRIBE.
//  Given a face image (a face region cropped from the visual scene), it produces
//  a 512-dimensional, L2-normalised embedding. Two faces are the same person if
//  their embeddings have high cosine similarity.
//
//  Model: glintr100.onnx (ResNet100 ArcFace, Apache-2.0, from fal/AuraFace-v1),
//  staged under D:\AI\Models\. Input 112x112 RGB, output a 512-float embedding.
//  ONNX Runtime + ImageSharp, matching the TIQ/SCRFD pattern.
//
//  NOT compile-verified / not run here. The 112x112 preprocessing and the
//  output being a single [1,512] tensor follow the documented AuraFace/ArcFace
//  contract; verify on first inference (dump OutputMetadata like the SCRFD test).
// ---------------------------------------------------------------------------
public sealed class ArcFaceRecogniser : IDisposable
{
    private readonly InferenceSession _session;
    private const int Size = 112;

    // ArcFace normalisation: (v - 127.5) / 128, RGB, CHW.
    private const float Mean = 127.5f;
    private const float Scale = 1.0f / 128.0f;

    public ArcFaceRecogniser(string modelPath)
    {
        _session = new InferenceSession(modelPath);
    }

    // Embedding for an already-cropped, roughly face-only image.
    public float[] Embed(Image<Rgb24> faceImage)
    {
        var input = new DenseTensor<float>(new[] { 1, 3, Size, Size });

        using var work = faceImage.Clone(ctx => ctx.Resize(Size, Size));
        work.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < Size; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < Size; x++)
                {
                    Rgb24 p = row[x];
                    input[0, 0, y, x] = (p.R - Mean) * Scale;
                    input[0, 1, y, x] = (p.G - Mean) * Scale;
                    input[0, 2, y, x] = (p.B - Mean) * Scale;
                }
            }
        });

        var inputName = _session.InputMetadata.Keys.First();
        var inputs = new[] { NamedOnnxValue.CreateFromTensor(inputName, input) };

        using var results = _session.Run(inputs);
        var raw = results.First().AsTensor<float>().ToArray();

        return L2Normalise(raw);
    }

    // Embedding from encoded image bytes (e.g. a cropped face's bytes).
    public float[] Embed(byte[] faceImageData)
    {
        using var img = Image.Load<Rgb24>(faceImageData);
        return Embed(img);
    }

    private static float[] L2Normalise(float[] v)
    {
        double sum = 0;
        for (int i = 0; i < v.Length; i++) sum += (double)v[i] * v[i];
        float norm = (float)Math.Sqrt(sum);
        if (norm < 1e-12f) return v;
        var outp = new float[v.Length];
        for (int i = 0; i < v.Length; i++) outp[i] = v[i] / norm;
        return outp;
    }

    // Cosine similarity of two L2-normalised embeddings = dot product.
    public static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) throw new ArgumentException("Embedding length mismatch.");
        float dot = 0;
        for (int i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return dot;
    }

    public void Dispose() => _session.Dispose();
}
