using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Mpai.Mmc.Esi;

// ---------------------------------------------------------------------------
//  wav2vec2 dimensional speech-emotion estimation (audeering w2v2-L-robust-12).
//
//  Given a raw mono 16 kHz speech signal, predicts arousal, dominance, and
//  valence in ~[0,1]. The speech-affect analogue of the HSEmotion / BlazePose
//  wrappers: one ONNX wrapped with ONNX Runtime.
//
//  ONNX signature (confirmed by probe):
//    input  'signal'        [1,-1]  raw mono 16 kHz float
//    output 'hidden_states' [1,1024] (embedding; unused here)
//    output 'logits'        [1,3]   = arousal, dominance, valence  (~0..1)
// ---------------------------------------------------------------------------
public sealed class Wav2Vec2EmotionEstimator : IDisposable
{
    private readonly InferenceSession _session;

    public Wav2Vec2EmotionEstimator(string modelPath)
    {
        _session = new InferenceSession(modelPath);
    }

    // Predict dimensional affect from mono 16 kHz samples (roughly [-1,1] float).
    public SpeechAffect Estimate(float[] samples)
    {
        if (samples.Length == 0) return new SpeechAffect();

        var input = new DenseTensor<float>(samples, new[] { 1, samples.Length });

        var inputName = _session.InputMetadata.Keys.First();
        using var results = _session.Run(new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(inputName, input)
        });

        // 'logits' = [arousal, dominance, valence].
        var logits = results.First(r => r.Name == "logits").AsTensor<float>();
        return new SpeechAffect
        {
            Arousal   = logits[0, 0],
            Dominance = logits[0, 1],
            Valence   = logits[0, 2]
        };
    }

    public void Dispose() => _session.Dispose();
}

// Dimensional speech affect: arousal, dominance, valence in ~[0,1].
public sealed class SpeechAffect
{
    public float Arousal { get; init; }
    public float Dominance { get; init; }
    public float Valence { get; init; }
}
