using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

// Cross-platform (Linux/macOS/Windows) version of TIQEngine.
// Image loading uses ImageSharp instead of System.Drawing.
public class TIQEngine : IDisposable
{
    private readonly InferenceSession visionSession;
    private readonly InferenceSession encoderSession;
    private readonly InferenceSession decoderSession;
    private readonly BlipTokenizer tokenizer;

    // Cached vision output for the current image (only the vision stage depends
    // on the image, so it is run once per image and reused for every question).
    private float[]? visionData;
    private int[]? visionDims;
    private string? currentImagePath;

    public string? CurrentImagePath => currentImagePath;

    public TIQEngine(
        string visionModel,
        string encoderModel,
        string decoderModel,
        string vocabFile)
    {
        visionSession = new InferenceSession(visionModel);

        encoderSession = new InferenceSession(encoderModel);

        decoderSession = new InferenceSession(decoderModel);

        tokenizer = new BlipTokenizer(vocabFile);    }

    // Load the current image from a file path.
    public void SetImage(string imageFile)
    {
        using var image = Image.Load<Rgb24>(imageFile);
        RunVision(image);
        currentImagePath = imageFile;
    }

    // Load the current image from encoded image bytes.
    public void SetImageFromBytes(byte[] imageData)
    {
        using var image = Image.Load<Rgb24>(imageData);
        RunVision(image);
        currentImagePath = null;
    }

    private void RunVision(Image<Rgb24> image)
    {
        var pixelValues = LoadBlipImage(image);

        var visionInputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("pixel_values", pixelValues)
        };

        using var visionResults = visionSession.Run(visionInputs);

        var v = visionResults.First().AsTensor<float>();

        visionDims = new int[v.Rank];

        for (int i = 0; i < v.Rank; i++)
        {
            visionDims[i] = v.Dimensions[i];
        }

        int length = 1;

        foreach (var d in visionDims)
        {
            length *= d;
        }

        visionData = new float[length];

        for (int i = 0; i < length; i++)
        {
            visionData[i] = v.GetValue(i);
        }
    }

    public string Ask(string question)
    {
        if (visionData is null || visionDims is null)
        {
            throw new InvalidOperationException(
                "No image loaded. Call SetImage(...) before Ask(...).");
        }

        var visionTensor =
            new DenseTensor<float>(
                visionData.AsMemory(),
                visionDims);

        var questionTokenIds =
            tokenizer.Encode(question);

        var inputIds =
            new DenseTensor<long>(
                new[] { 1, questionTokenIds.Length });

        var attentionMask =
            new DenseTensor<long>(
                new[] { 1, questionTokenIds.Length });

        for (int i = 0; i < questionTokenIds.Length; i++)
        {
            inputIds[0, i] = questionTokenIds[i];
            attentionMask[0, i] = 1;
        }

var encoderInputs =
    new List<NamedOnnxValue>
    {
        NamedOnnxValue.CreateFromTensor(
            "input_ids",
            inputIds),

        NamedOnnxValue.CreateFromTensor(
            "attention_mask",
            attentionMask),

        NamedOnnxValue.CreateFromTensor(
            "encoder_hidden_states",
            visionTensor)
    };
        using var encoderResults =
            encoderSession.Run(encoderInputs);

        var context =
            encoderResults.First().AsTensor<float>();

        var generated =
            new List<long> { 30522 };

        const int sepToken = 102;
        const int maxSteps = 40;

        for (int step = 0; step < maxSteps; step++)
        {
            var decoderInput =
                new DenseTensor<long>(
                    new[] { 1, generated.Count });

            for (int i = 0; i < generated.Count; i++)
            {
                decoderInput[0, i] = generated[i];
            }

            var decoderInputs =
                new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(
                        "input_ids",
                        decoderInput),

                    NamedOnnxValue.CreateFromTensor(
                        "encoder_hidden_states",
                        context)
                };

            using var decoderResults =
                decoderSession.Run(decoderInputs);

            var logits =
                decoderResults.First().AsTensor<float>();

            int lastPosition =
                logits.Dimensions[1] - 1;

            int bestToken = -1;
            float bestValue = float.MinValue;

            for (int token = 0;
                 token < logits.Dimensions[2];
                 token++)
            {
                float value =
                    logits[0, lastPosition, token];

                if (value > bestValue)
                {
                    bestValue = value;
                    bestToken = token;
                }
            }

            generated.Add(bestToken);

            if (bestToken == sepToken)
            {
                break;
            }
        }

        return tokenizer.Decode(generated);
    }

    public string Query(
        string imageFile,
        string question)
    {
        SetImage(imageFile);

        return Ask(question);
    }

    public void Dispose()
    {
        visionSession.Dispose();
        encoderSession.Dispose();
        decoderSession.Dispose();
    }

    // Resize to 384x384 and normalise with BLIP's mean/std into [1,3,384,384].
    private static DenseTensor<float> LoadBlipImage(
        Image<Rgb24> source)
    {
        const int size = 384;

        float[] mean =
        {
            0.48145466f,
            0.45782750f,
            0.40821073f
        };

        float[] std =
        {
            0.26862954f,
            0.26130258f,
            0.27577711f
        };

        var tensor =
            new DenseTensor<float>(
                new[] { 1, 3, size, size });

        // Work on a resized clone so the caller's image is untouched.
        using var image =
            source.Clone(
                ctx => ctx.Resize(size, size));

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < size; y++)
            {
                var row =
                    accessor.GetRowSpan(y);

                for (int x = 0; x < size; x++)
                {
                    Rgb24 p = row[x];

                    tensor[0, 0, y, x] =
                        (p.R / 255f - mean[0]) / std[0];

                    tensor[0, 1, y, x] =
                        (p.G / 255f - mean[1]) / std[1];

                    tensor[0, 2, y, x] =
                        (p.B / 255f - mean[2]) / std[2];
                }
            }
        });

        return tensor;
    }
}