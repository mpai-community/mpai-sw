using System.Collections.Generic;

namespace Mpai.Core;

// MMC-RTX-V2.5 — Recognised Text.
// The structured output of the OCR AIM: the lines of text found in an image,
// each with a confidence value and a bounding box.
public sealed class RecognisedText
{
    public string Header { get; init; } = "MMC-RTX-V2.5";

    public string? MInstanceID      { get; init; }
    public string? UEnvironmentID   { get; init; }
    public string? RecognisedTextID { get; init; }

    public List<TextLine> TextLines { get; init; } = new();
}

// One recognised line of text.
public sealed class TextLine
{
    public BasicTextObject Text        { get; init; } = new();
    public double          Confidence  { get; init; }
    public BoundingBox     BoundingBox { get; init; } = new();
}

// Pixel bounding box of a recognised line, origin top-left.
public sealed class BoundingBox
{
    public int X      { get; init; }
    public int Y      { get; init; }
    public int Width  { get; init; }
    public int Height { get; init; }
}
