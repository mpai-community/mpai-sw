using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Mpai.Mmc.Efi;

// ---------------------------------------------------------------------------
//  HSEmotion facial-affect estimation (EfficientNet-B0, multi-task).
//
//  Given an image cropped to one face, predicts eight emotion probabilities plus
//  valence and arousal, from the HSEmotion enet_b0_8_va_mtl model (Apache-2.0,
//  AffectNet-trained). The face-affect analogue of the BlazePose / YOLOX wrappers:
//  one ONNX wrapped with ONNX Runtime + ImageSharp.
//
//  ONNX signature (confirmed by probe):
//    input  [1,3,224,224]  NCHW RGB, ImageNet-normalised
//    output [1,10]         8 emotion logits + valence + arousal
//  Emotion order (AffectNet 8): Anger, Contempt, Disgust, Fear, Happiness,
//  Neutral, Sadness, Surprise.
// ---------------------------------------------------------------------------
public sealed class HSEmotionEstimator : IDisposable
{
    private readonly InferenceSession _session;
    private const int InputSize = 224;

    // ImageNet normalisation (the EfficientNet AffectNet models use this).
    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std  = { 0.229f, 0.224f, 0.225f };

    public static readonly string[] EmotionLabels =
    { "Anger", "Contempt", "Disgust", "Fear", "Happiness", "Neutral", "Sadness", "Surprise" };

    public HSEmotionEstimator(string modelPath)
    {
        _session = new InferenceSession(modelPath);
    }

    // Estimate facial affect from an image already cropped to one face.
    public FaceAffect Estimate(byte[] faceCropPng)
    {
        using var image = Image.Load<Rgb24>(faceCropPng);
        return Estimate(image);
    }

    public FaceAffect Estimate(Image<Rgb24> image)
    {
        using var work = image.Clone(ctx => ctx.Resize(InputSize, InputSize));
        var input = new DenseTensor<float>(new[] { 1, 3, InputSize, InputSize });
        work.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < InputSize; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < InputSize; x++)
                {
                    Rgb24 p = row[x];
                    input[0, 0, y, x] = (p.R / 255f - Mean[0]) / Std[0];
                    input[0, 1, y, x] = (p.G / 255f - Mean[1]) / Std[1];
                    input[0, 2, y, x] = (p.B / 255f - Mean[2]) / Std[2];
                }
            }
        });

        var inputName = _session.InputMetadata.Keys.First();
        using var results = _session.Run(new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(inputName, input)
        });

        var output = results.First().AsTensor<float>();   // [1,10]
        var logits = new float[8];
        for (int i = 0; i < 8; i++) logits[i] = output[0, i];
        float valence = output[0, 8];
        float arousal = output[0, 9];

        var probs = Softmax(logits);
        int best = Array.IndexOf(probs, probs.Max());

        return new FaceAffect
        {
            Emotion = EmotionLabels[best],
            Confidence = probs[best],
            Valence = valence,
            Arousal = arousal,
            Probabilities = EmotionLabels.Zip(probs, (l, p) => (l, p)).ToDictionary(t => t.l, t => t.p)
        };
    }

    private static float[] Softmax(float[] logits)
    {
        float max = logits.Max();
        var exp = logits.Select(v => (float)Math.Exp(v - max)).ToArray();
        float sum = exp.Sum();
        return exp.Select(v => v / sum).ToArray();
    }

    public void Dispose() => _session.Dispose();
}

// The facial affect read from a face: the top emotion label + its confidence,
// plus the valence and arousal dimensions and the full probability map.
public sealed class FaceAffect
{
    public string Emotion { get; init; } = "";
    public float Confidence { get; init; }
    public float Valence { get; init; }
    public float Arousal { get; init; }
    public Dictionary<string, float> Probabilities { get; init; } = new();
}
