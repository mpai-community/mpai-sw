using System.Collections.Generic;

namespace Mpai.Core.OSD;

// ---------------------------------------------------------------------------
//  Bounding Box - OSD/V1.5/data/BoundingBox.json  (Header OSD-BBX-V1.5)
//
//  The region surrounding a Visual Object in a scene, 2D (rectangle) or 3D
//  (right parallelepiped). Carries the Visual Object it surrounds (VisualData)
//  and the content format(s) of that object (BBXContentFormats) - which is where
//  a face crop's JPEG is recorded, exactly as the object's own VisualQualifier
//  records it. Emitted by PAF-FIR to tell a downstream AIM WHICH face an identity
//  belongs to.
//
//  NB the pre-existing `BoundingBox` in Core/RecognisedText.cs is a TEXT/OCR box
//  (to be renamed TextBoundingBox) and is unrelated to this OSD-BBX-V1.5 type.
// ---------------------------------------------------------------------------
public sealed class BoundingBox
{
    public string  Header         { get; init; } = "OSD-BBX-V1.5";
    public string? MInstanceID    { get; init; }
    public string? UEnvironmentID { get; init; }
    public string  BoundingBoxID  { get; init; } = "";

    public SimpleTime? BoundingBoxTime      { get; init; }
    public SimpleTime? BoundingBoxSpaceTime { get; init; }

    public string Dimensions { get; init; } = "2D";   // "2D" | "3D"

    // 3D bounding volume - valid only when Dimensions == "3D".
    public object? RightParallelepiped { get; init; }

    // The Visual Object surrounded by the box (e.g. the face crop). May be absent.
    public BasicVisualObject? VisualData { get; init; }

    // Format(s) of the Visual Object content within the box (>= 1).
    // For a 2D box: entries carry a 2D format (e.g. JPEG).
    public List<BBXContentFormat> BBXContentFormats { get; init; } = new();

    public DataExchangeMetadata? DataXMData { get; init; }
    public string?               DescrMetadata { get; init; }

    // Convenience: a 2D box around a JPEG face crop.
    public static BoundingBox For2DFace(BasicVisualObject faceCrop, string? id = null) => new()
    {
        BoundingBoxID = id ?? System.Guid.NewGuid().ToString(),
        Dimensions = "2D",
        VisualData = faceCrop,
        BBXContentFormats = new List<BBXContentFormat>
        {
            new BBXContentFormat { TwoD = Visual2DStaticFormat.JPEG }
        }
    };
}

// One entry of BBXContentFormats: exactly a 2D or a 3D format.
public sealed class BBXContentFormat
{
    public Visual2DStaticFormat? TwoD   { get; init; }   // schema key "2D"
    public string?               ThreeD { get; init; }   // schema key "3D" (Visual3DStaticFormats)
}
