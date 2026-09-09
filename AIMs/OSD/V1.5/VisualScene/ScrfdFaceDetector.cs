using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Mpai.Osd.VisualScene;

// ---------------------------------------------------------------------------
//  SCRFD face DETECTION (not recognition).
//
//  Locates faces in an image and returns, per face, a bounding box, a score,
//  and 5 landmarks - i.e. it DESCRIBES where faces are. It does NOT identify
//  whose face it is; that is FIR's job downstream.
//
//  Model: scrfd_10g_bnkps.onnx (Apache-2.0, from fal/AuraFace-v1), staged under
//  D:\AI\Models\. ONNX Runtime + ImageSharp, matching the TIQ/BLIP pattern.
//
//  NOT COMPILE-VERIFIED and NOT RUN here. The post-processing (anchor decode
//  across strides 8/16/32 + NMS) is written to the standard SCRFD-bnkps output
//  layout; the exact output tensor names/order MUST be verified on the first
//  real inference - this is the one part most likely to need tuning against the
//  actual model. See VerifyOutputs() note.
// ---------------------------------------------------------------------------
public sealed class ScrfdFaceDetector : IDisposable
{
    private readonly InferenceSession _session;
    private readonly int _inputSize;      // SCRFD is square; 640 for scrfd_10g
    private readonly float _scoreThreshold;
    private readonly float _nmsThreshold;

    // SCRFD normalisation (fixed for the InsightFace SCRFD family).
    private const float Mean = 127.5f;
    private const float Scale = 1.0f / 128.0f;

    // Anchors: 2 per location, strides 8/16/32 (SCRFD-bnkps standard config).
    private static readonly int[] Strides = { 8, 16, 32 };
    private const int NumAnchors = 2;

    public ScrfdFaceDetector(
        string modelPath,
        int inputSize = 640,
        float scoreThreshold = 0.5f,
        float nmsThreshold = 0.4f)
    {
        _session = new InferenceSession(modelPath);
        _inputSize = inputSize;
        _scoreThreshold = scoreThreshold;
        _nmsThreshold = nmsThreshold;
    }

    // Detect faces in an encoded image (e.g. the bytes from BasicVisualObject.Data).
    public IReadOnlyList<FaceDetection> Detect(byte[] imageData)
    {
        using var image = Image.Load<Rgb24>(imageData);
        return Detect(image);
    }

    public IReadOnlyList<FaceDetection> Detect(Image<Rgb24> image)
    {
        int origW = image.Width, origH = image.Height;

        // Letterbox to a square _inputSize, preserving aspect ratio, so boxes
        // can be mapped straight back to original pixels.
        float ratio = Math.Min((float)_inputSize / origW, (float)_inputSize / origH);
        int resizedW = (int)Math.Round(origW * ratio);
        int resizedH = (int)Math.Round(origH * ratio);

        var input = new DenseTensor<float>(new[] { 1, 3, _inputSize, _inputSize });

        using var work = image.Clone(ctx => ctx.Resize(resizedW, resizedH));
        work.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < resizedH; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < resizedW; x++)
                {
                    Rgb24 p = row[x];
                    // SCRFD expects RGB, (v - 127.5)/128, CHW. Pad stays 0.
                    input[0, 0, y, x] = (p.R - Mean) * Scale;
                    input[0, 1, y, x] = (p.G - Mean) * Scale;
                    input[0, 2, y, x] = (p.B - Mean) * Scale;
                }
            }
        });

        var inputName = _session.InputMetadata.Keys.First();
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(inputName, input)
        };

        using var results = _session.Run(inputs);

        var faces = DecodeScrfd(results, ratio);
        return Nms(faces, _nmsThreshold);
    }

    // ---- SCRFD decode -------------------------------------------------------
    // SCRFD-bnkps outputs, per stride s in {8,16,32}: score (N,1), bbox (N,4),
    // kps (N,10). Output ORDER for the standard export is:
    //   [score_8, score_16, score_32, bbox_8, bbox_16, bbox_32, kps_8, kps_16, kps_32]
    // VERIFY this on first run against _session.OutputMetadata (names encode the
    // stride); if the order differs, index by name instead of position.
    private List<FaceDetection> DecodeScrfd(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, float ratio)
    {
        var outputs = results.ToList();
        var faces = new List<FaceDetection>();

        // Pull the three groups by position (see note above).
        // scores: 0..2, bboxes: 3..5, kps: 6..8
        for (int i = 0; i < Strides.Length; i++)
        {
            int stride = Strides[i];
            var scores = outputs[i].AsTensor<float>();
            var bboxes = outputs[3 + i].AsTensor<float>();
            var kps    = outputs[6 + i].AsTensor<float>();

            int featW = _inputSize / stride;
            int featH = _inputSize / stride;
            int idx = 0;

            for (int y = 0; y < featH; y++)
            for (int x = 0; x < featW; x++)
            for (int a = 0; a < NumAnchors; a++, idx++)
            {
                float score = scores.ElementAt(idx);
                if (score < _scoreThreshold) continue;

                // Anchor centre (in input pixels).
                float cx = x * stride;
                float cy = y * stride;

                // Distance-to-box decode: bbox = [l, t, r, b] as distances * stride.
                float l = bboxes.ElementAt(idx * 4 + 0) * stride;
                float t = bboxes.ElementAt(idx * 4 + 1) * stride;
                float r = bboxes.ElementAt(idx * 4 + 2) * stride;
                float b = bboxes.ElementAt(idx * 4 + 3) * stride;

                float x1 = (cx - l) / ratio;
                float y1 = (cy - t) / ratio;
                float x2 = (cx + r) / ratio;
                float y2 = (cy + b) / ratio;

                var landmarks = new (float X, float Y)[5];
                for (int k = 0; k < 5; k++)
                {
                    float lx = (cx + kps.ElementAt(idx * 10 + k * 2 + 0) * stride) / ratio;
                    float ly = (cy + kps.ElementAt(idx * 10 + k * 2 + 1) * stride) / ratio;
                    landmarks[k] = (lx, ly);
                }

                faces.Add(new FaceDetection
                {
                    X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                    Score = score,
                    Landmarks = landmarks
                });
            }
        }
        return faces;
    }

    // ---- NMS ----------------------------------------------------------------
    private static List<FaceDetection> Nms(List<FaceDetection> faces, float iouThresh)
    {
        var ordered = faces.OrderByDescending(f => f.Score).ToList();
        var kept = new List<FaceDetection>();
        while (ordered.Count > 0)
        {
            var best = ordered[0];
            kept.Add(best);
            ordered.RemoveAt(0);
            ordered.RemoveAll(f => IoU(best, f) > iouThresh);
        }
        return kept;
    }

    private static float IoU(FaceDetection a, FaceDetection b)
    {
        float ix1 = Math.Max(a.X1, b.X1), iy1 = Math.Max(a.Y1, b.Y1);
        float ix2 = Math.Min(a.X2, b.X2), iy2 = Math.Min(a.Y2, b.Y2);
        float iw = Math.Max(0, ix2 - ix1), ih = Math.Max(0, iy2 - iy1);
        float inter = iw * ih;
        float areaA = (a.X2 - a.X1) * (a.Y2 - a.Y1);
        float areaB = (b.X2 - b.X1) * (b.Y2 - b.Y1);
        float union = areaA + areaB - inter;
        return union <= 0 ? 0 : inter / union;
    }

    public void Dispose() => _session.Dispose();
}

// A described face: where it is (box + landmarks) and how confident - NO identity.
public sealed class FaceDetection
{
    public float X1 { get; init; }
    public float Y1 { get; init; }
    public float X2 { get; init; }
    public float Y2 { get; init; }
    public float Score { get; init; }
    public (float X, float Y)[] Landmarks { get; init; } = Array.Empty<(float, float)>();

    public float CentreX => (X1 + X2) / 2f;
    public float CentreY => (Y1 + Y2) / 2f;
    public float Width  => X2 - X1;
    public float Height => Y2 - Y1;
}
