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
//  YOLOX general-object DETECTION (not identification of WHICH instance).
//
//  Locates objects in an image and returns, per object, a bounding box, a COCO
//  class, and a score - i.e. it DESCRIBES what objects are where. It is the
//  general-object sibling of ScrfdFaceDetector: SCRFD finds faces, YOLOX finds
//  the 80 COCO categories (person, car, bicycle, ...). Identifying WHICH car or
//  WHOSE face is downstream work; this only says "a car is here".
//
//  Model: yolox_s.onnx (Apache-2.0, Megvii YOLOX), staged under D:\AI\Models\.
//  ONNX Runtime + ImageSharp, matching the ScrfdFaceDetector pattern exactly.
//
//  NOT COMPILE-VERIFIED and NOT RUN here. Two parts are the standard places a
//  YOLOX port needs tuning against the actual export, flagged inline:
//    (a) preprocessing - pad colour and whether the export wants raw 0-255 or
//        normalised input, and RGB vs BGR channel order;
//    (b) the output decode - whether the ONNX already grid-decoded the centres
//        (output [1,8400,85] in pixel units) or emits raw grid predictions that
//        need the grid-offset + stride reconstruction this code performs.
//  Verify both on first real inference (see VerifyOutputs note at the decode).
// ---------------------------------------------------------------------------
public sealed class YoloxObjectDetector : IDisposable
{
    private readonly InferenceSession _session;
    private readonly int _inputSize;        // YOLOX-s is square; 640 default
    private readonly float _scoreThreshold;
    private readonly float _nmsThreshold;

    // YOLOX strides (same family as SCRFD): grids at 8/16/32 over the input.
    private static readonly int[] Strides = { 8, 16, 32 };

    // YOLOX-s standard export: raw pixel values (no mean/scale), pad colour 114.
    // If the model was exported WITH normalisation, set these via a flag later.
    private const float PadValue = 114f;

    public YoloxObjectDetector(
        string modelPath,
        int inputSize = 640,
        float scoreThreshold = 0.3f,
        float nmsThreshold = 0.45f)
    {
        _session = new InferenceSession(modelPath);
        _inputSize = inputSize;
        _scoreThreshold = scoreThreshold;
        _nmsThreshold = nmsThreshold;
    }

    public IReadOnlyList<ObjectDetection> Detect(byte[] imageData)
    {
        using var image = Image.Load<Rgb24>(imageData);
        return Detect(image);
    }

    public IReadOnlyList<ObjectDetection> Detect(Image<Rgb24> image)
    {
        int origW = image.Width, origH = image.Height;

        // Letterbox to a square _inputSize, preserving aspect ratio, padding with
        // 114 (YOLOX convention), so boxes map straight back to original pixels.
        float ratio = Math.Min((float)_inputSize / origW, (float)_inputSize / origH);
        int resizedW = (int)Math.Round(origW * ratio);
        int resizedH = (int)Math.Round(origH * ratio);

        var input = new DenseTensor<float>(new[] { 1, 3, _inputSize, _inputSize });

        // Fill with pad colour first (top-left aligned letterbox, pad bottom/right).
        for (int c = 0; c < 3; c++)
            for (int y = 0; y < _inputSize; y++)
                for (int x = 0; x < _inputSize; x++)
                    input[0, c, y, x] = PadValue;

        using var work = image.Clone(ctx => ctx.Resize(resizedW, resizedH));
        work.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < resizedH; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < resizedW; x++)
                {
                    Rgb24 p = row[x];
                    // YOLOX-s standard export: raw values, CHW, RGB order.
                    // (If detections come back empty/garbage, try BGR: swap R and B.)
                    input[0, 0, y, x] = p.R;
                    input[0, 1, y, x] = p.G;
                    input[0, 2, y, x] = p.B;
                }
            }
        });

        var inputName = _session.InputMetadata.Keys.First();
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(inputName, input)
        };

        using var results = _session.Run(inputs);

        var detections = DecodeYolox(results, ratio);
        return Nms(detections, _nmsThreshold);
    }

    // ---- YOLOX decode -------------------------------------------------------
    // Standard YOLOX output is a single tensor [1, N, 85] where 85 = 4 box
    // (cx, cy, w, h) + 1 objectness + 80 class scores, and N = sum over strides
    // {8,16,32} of gridH*gridW (8400 for a 640 input). The centres/sizes are in
    // GRID units and need the grid offset added and multiplication by stride -
    // this is what the loop below reconstructs.
    //
    // VerifyOutputs: if yolox_s.onnx was exported with --decode_in_inference, the
    // boxes are ALREADY in input-pixel units and the grid/stride add-back here is
    // wrong (boxes will be tiny/near-origin). In that case, skip the grid math and
    // use raw cx,cy,w,h directly. Check on first run.
    private List<ObjectDetection> DecodeYolox(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, float ratio)
    {
        var output = results.First().AsTensor<float>();   // [1, N, 85]
        int n = output.Dimensions[1];
        int dim = output.Dimensions[2];                    // 85 for COCO
        int numClasses = dim - 5;

        // Build the (gridX, gridY, stride) table in the SAME order YOLOX flattens:
        // stride 8 grid first, then 16, then 32; row-major within each grid.
        var grid = BuildGrid(_inputSize);
        // grid.Count must equal n; if not, the export differs - fall back to no
        // grid decode (treat cx,cy,w,h as already in pixels).
        bool gridDecoded = grid.Count == n;

        var detections = new List<ObjectDetection>();

        for (int i = 0; i < n; i++)
        {
            float cx = output[0, i, 0];
            float cy = output[0, i, 1];
            float w  = output[0, i, 2];
            float h  = output[0, i, 3];
            float objectness = output[0, i, 4];

            if (gridDecoded)
            {
                var (gx, gy, stride) = grid[i];
                cx = (cx + gx) * stride;
                cy = (cy + gy) * stride;
                w  = (float)Math.Exp(w) * stride;
                h  = (float)Math.Exp(h) * stride;
            }

            // Best class.
            int bestClass = -1;
            float bestClassScore = 0f;
            for (int c = 0; c < numClasses; c++)
            {
                float cs = output[0, i, 5 + c];
                if (cs > bestClassScore) { bestClassScore = cs; bestClass = c; }
            }

            float score = objectness * bestClassScore;
            if (score < _scoreThreshold || bestClass < 0) continue;

            // Centre form -> corner form, then map back to original pixels.
            float x1 = (cx - w / 2f) / ratio;
            float y1 = (cy - h / 2f) / ratio;
            float x2 = (cx + w / 2f) / ratio;
            float y2 = (cy + h / 2f) / ratio;

            detections.Add(new ObjectDetection
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                Score = score,
                ClassId = bestClass,
                ClassName = bestClass < CocoClasses.Length ? CocoClasses[bestClass] : $"class_{bestClass}"
            });
        }
        return detections;
    }

    // The (gridX, gridY, stride) for each of the N flattened predictions, in
    // YOLOX order: strides 8,16,32; within a stride, y-major then x.
    private static List<(int Gx, int Gy, int Stride)> BuildGrid(int inputSize)
    {
        var grid = new List<(int, int, int)>();
        foreach (int stride in Strides)
        {
            int gh = inputSize / stride;
            int gw = inputSize / stride;
            for (int y = 0; y < gh; y++)
                for (int x = 0; x < gw; x++)
                    grid.Add((x, y, stride));
        }
        return grid;
    }

    // ---- NMS (class-aware) --------------------------------------------------
    private static List<ObjectDetection> Nms(List<ObjectDetection> dets, float iouThresh)
    {
        var kept = new List<ObjectDetection>();
        // NMS per class, so a person overlapping a car does not suppress the car.
        foreach (var group in dets.GroupBy(d => d.ClassId))
        {
            var ordered = group.OrderByDescending(d => d.Score).ToList();
            while (ordered.Count > 0)
            {
                var best = ordered[0];
                kept.Add(best);
                ordered.RemoveAt(0);
                ordered.RemoveAll(d => IoU(best, d) > iouThresh);
            }
        }
        return kept.OrderByDescending(d => d.Score).ToList();
    }

    private static float IoU(ObjectDetection a, ObjectDetection b)
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

    // COCO 80 classes, in the standard YOLOX/COCO order.
    private static readonly string[] CocoClasses =
    {
        "person","bicycle","car","motorcycle","airplane","bus","train","truck","boat",
        "traffic light","fire hydrant","stop sign","parking meter","bench","bird","cat",
        "dog","horse","sheep","cow","elephant","bear","zebra","giraffe","backpack","umbrella",
        "handbag","tie","suitcase","frisbee","skis","snowboard","sports ball","kite",
        "baseball bat","baseball glove","skateboard","surfboard","tennis racket","bottle",
        "wine glass","cup","fork","knife","spoon","bowl","banana","apple","sandwich","orange",
        "broccoli","carrot","hot dog","pizza","donut","cake","chair","couch","potted plant",
        "bed","dining table","toilet","tv","laptop","mouse","remote","keyboard","cell phone",
        "microwave","oven","toaster","sink","refrigerator","book","clock","vase","scissors",
        "teddy bear","hair drier","toothbrush"
    };
}

// A described object: where it is (box), what class, and how confident - NO
// instance identity. The general-object analogue of FaceDetection.
public sealed class ObjectDetection
{
    public float X1 { get; init; }
    public float Y1 { get; init; }
    public float X2 { get; init; }
    public float Y2 { get; init; }
    public float Score { get; init; }
    public int ClassId { get; init; }
    public string ClassName { get; init; } = "";

    public float CentreX => (X1 + X2) / 2f;
    public float CentreY => (Y1 + Y2) / 2f;
    public float Width  => X2 - X1;
    public float Height => Y2 - Y1;
}
