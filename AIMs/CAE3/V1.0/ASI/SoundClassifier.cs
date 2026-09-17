using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Mpai.Cae.Asi;

// Loads YAMNet (yamnet.onnx): input raw mono 16 kHz waveform [-1], output_0
// per-frame scores [-1, 521] over the AudioSet classes (the mel front-end is
// internal, so no C# DSP). Mean-aggregates scores across frames and returns the
// ranked top classes with their display-name labels (from yamnet_class_map.csv).
public sealed class SoundClassifier : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string[] _labels;   // index -> display_name

    // Indices of the AudioSet speech FAMILY (Speech, Child speech, Conversation,
    // Narration, Speech synthesizer, ...) - a positive here means "route to SIR".
    private static readonly HashSet<int> SpeechFamily = new() { 0, 1, 2, 3, 4, 5 };

    public SoundClassifier(string modelPath, string classMapPath)
    {
        _session = new InferenceSession(modelPath);
        _inputName = _session.InputMetadata.Keys.First();
        _labels = LoadLabels(classMapPath);
    }

    public sealed class ClassResult
    {
        public int Index { get; init; }
        public string Label { get; init; } = "";
        public float Score { get; init; }
        public bool IsSpeech => SpeechFamily.Contains(Index);
    }

    // Classify a mono 16 kHz waveform. Returns the top-k classes, highest first.
    public List<ClassResult> Classify(float[] samples, int topK = 5)
    {
        var tensor = new DenseTensor<float>(samples, new[] { samples.Length });
        var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };
        using var results = _session.Run(inputs);

        // output_0: [frames, 521]. Mean over frames.
        var outv = results.First(r => r.Name == _session.OutputMetadata.Keys.First());
        var t = outv.AsTensor<float>();
        var dims = t.Dimensions.ToArray();
        int frames = dims[0], classes = dims[1];

        var mean = new float[classes];
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < classes; c++)
                mean[c] += t[f, c];
        for (int c = 0; c < classes; c++) mean[c] /= Math.Max(frames, 1);

        return Enumerable.Range(0, classes)
            .Select(c => new ClassResult { Index = c, Label = c < _labels.Length ? _labels[c] : $"#{c}", Score = mean[c] })
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .ToList();
    }

    private static string[] LoadLabels(string csvPath)
    {
        var labels = new List<string>();
        bool header = true;
        foreach (var line in File.ReadLines(csvPath))
        {
            if (header) { header = false; continue; }
            // index,mid,display_name  - display_name may be quoted and contain commas.
            int firstComma = line.IndexOf(',');
            int secondComma = line.IndexOf(',', firstComma + 1);
            if (secondComma < 0) continue;
            string name = line[(secondComma + 1)..].Trim();
            if (name.StartsWith('"') && name.EndsWith('"')) name = name[1..^1];
            labels.Add(name);
        }
        return labels.ToArray();
    }

    public void Dispose() => _session.Dispose();
}
