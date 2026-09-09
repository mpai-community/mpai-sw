using System;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Mpai.Paf.Fir;

// ---------------------------------------------------------------------------
//  Crops a detected face region out of the source image, so the recogniser
//  receives just the face rather than the whole frame. Also the extension point
//  flagged in the OSD visual pipeline (which currently attaches the whole image
//  per face). Pure ImageSharp, no model.
//
//  Takes box coordinates in ORIGINAL-image pixels (as ScrfdFaceDetector returns)
//  and an optional margin to include a little context around the face, which
//  ArcFace-family models generally prefer.
// ---------------------------------------------------------------------------
public static class FaceCrop
{
    public static Image<Rgb24> Crop(
        Image<Rgb24> source,
        float x1, float y1, float x2, float y2,
        float marginFraction = 0.2f)
    {
        int w = source.Width, h = source.Height;

        float bw = x2 - x1, bh = y2 - y1;
        float mx = bw * marginFraction, my = bh * marginFraction;

        int cx1 = (int)Math.Round(Math.Max(0, x1 - mx));
        int cy1 = (int)Math.Round(Math.Max(0, y1 - my));
        int cx2 = (int)Math.Round(Math.Min(w, x2 + mx));
        int cy2 = (int)Math.Round(Math.Min(h, y2 + my));

        int cw = Math.Max(1, cx2 - cx1);
        int ch = Math.Max(1, cy2 - cy1);

        return source.Clone(ctx => ctx.Crop(new Rectangle(cx1, cy1, cw, ch)));
    }

    // Convenience: crop from encoded image bytes.
    public static Image<Rgb24> Crop(
        byte[] imageData,
        float x1, float y1, float x2, float y2,
        float marginFraction = 0.2f)
    {
        using var img = Image.Load<Rgb24>(imageData);
        return Crop(img, x1, y1, x2, y2, marginFraction);
    }
}
