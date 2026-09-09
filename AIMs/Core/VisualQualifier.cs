using System;

namespace Mpai.Core;

// ---------------------------------------------------------------------------
//  Visual Qualifier Ã¢â‚¬â€ TFA/V1.5/data/VisualQualifier.json
//
//  The running description of a Visual Object: its colour characteristics
//  (SubType), its content and transport formats (Format), and its source and
//  device attributes (Attributes). Projected faithfully from the TFA schema,
//  in the same style as TextQualifier and SpeechQualifier.
//
//  A Visual Object is Basic Visual Data + this Qualifier Ã¢â‚¬â€ not just bytes.
// ---------------------------------------------------------------------------
public sealed class VisualQualifier
{
    public string  Header            { get; init; } = "TFA-VIQ-V1.5";
    public string? MInstanceID       { get; init; }
    public string? UEnvironmentID    { get; init; }
    public string  VisualQualifierID { get; init; } = "";
    public SpaceTime? VisualQualifierTime { get; init; }

    public VisualSubType?    SubType    { get; init; }
    public VisualFormat?     Format     { get; init; }
    public VisualAttributes? Attributes { get; init; }

    public DataExchangeMetadata? DataXMData { get; init; }
    public string? DescrMetadata { get; init; }

    // Convenience factory: build a minimal but valid Visual Qualifier for a
    // 2D raster still image of a known format (PNG/JPEG/BMP/...), which is the
    // common case for acquired screenshots and photos.
    public static VisualQualifier For2DStill(
        Visual2DStaticFormat format,
        VisualFileFormat?     fileFormat = null,
        ColourFormat?         colour     = null,
        string?               visualObjectType = null) => new()
    {
        VisualQualifierID = Guid.NewGuid().ToString(),
        SubType = colour is null ? null : new VisualSubType { ColourFormat = colour },
        Format = new VisualFormat
        {
            Content = new VisualContentFormat
            {
                TwoD = new Visual2D { Static = format }
            },
            Transport = fileFormat is null ? null : new VisualTransport { FileFormat = fileFormat }
        },
        Attributes = new VisualAttributes
        {
            Source = new VisualSource { Real = "Raster" },
            VisualObjectType = visualObjectType   // schema Attributes.VisualObjectType ("Face"|"Body"|"Object")
        }
    };
}

// Ã¢â€â‚¬Ã¢â€â‚¬ SubType Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬
public sealed class VisualSubType
{
    public ColourFormat? ColourFormat      { get; init; }
    public double?       AlphaChannel      { get; init; }   // 0..1
    public double?       Brightness        { get; init; }
    public string?       ColourSubsampling { get; init; }   // "4:4:4" | "4:2:2" | "4:2:0" | "4:1:1"
    public string?       YUV               { get; init; }   // "Y'UV" | "Y'PbPr" | "Y'CbCr" | "YDbDr" | "Y'IQ"
    public string?       CMYK              { get; init; }
}

// Ã¢â€â‚¬Ã¢â€â‚¬ Format Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬
public sealed class VisualFormat
{
    public VisualContentFormat? Content   { get; init; }
    public VisualTransport?     Transport { get; init; }
}

public sealed class VisualContentFormat
{
    public VisualTimeSampling? TimeSampling { get; init; }
    public Visual2D?           TwoD         { get; init; }   // schema key "2D"
    public Visual3D?           ThreeD       { get; init; }   // schema key "3D"
}

public sealed class VisualTimeSampling
{
    public BitsPerPixel[]? Precision { get; init; }
    public double?         Time      { get; init; }
    public double?         Space     { get; init; }
}

public sealed class BitsPerPixel
{
    public int BitsPerPixelValue { get; init; }   // schema key "bits-per-pixel"
}

public sealed class Visual2D
{
    public Visual2DStaticFormat? Static  { get; init; }
    public Visual2DDynamic?      Dynamic { get; init; }
}

public sealed class Visual2DDynamic
{
    public string? OtherContentFormat { get; init; }   // Visual2DDynamicFormats
}

public sealed class Visual3D
{
    public Visual3DStatic?  Static  { get; init; }
    public Visual3DDynamic? Dynamic { get; init; }
}

public sealed class Visual3DStatic
{
    public object? MPAIContentFormat   { get; init; }   // BoundingBox
    public object? MPAIGeometryFormat  { get; init; }   // VisualGeometry
    public string? OtherContentFormats { get; init; }   // Visual3DStaticFormats
}

public sealed class Visual3DDynamic
{
    public string? OtherContentFormats { get; init; }   // Visual3DDynamicFormats
}

public sealed class VisualTransport
{
    public VisualFileFormat? FileFormat   { get; init; }
    public string?           StreamFormat { get; init; }   // VisualStreamFormats
}

// Ã¢â€â‚¬Ã¢â€â‚¬ Attributes Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬
public sealed class VisualAttributes
{
    public VisualSource?       Source              { get; init; }
    public string?             VisualObjectType    { get; init; }   // "Face" | "Body" | "Object" - set by CVE-VSI (SCRFD)
    public string?             Metadata            { get; init; }   // VisualMetadataFormats
    public InstanceIdentifier? ObjectID            { get; init; }
    public PersonalStatus?     EntityInternalStatus { get; init; }
    public VisualDevice?       Device              { get; init; }
}

public sealed class VisualSource
{
    public string? Real      { get; init; }   // "Raster"
    public string? Synthetic { get; init; }   // "Raster" | "Vector"
}

public sealed class VisualDevice
{
    public string? DeviceID     { get; init; }
    public string? DeviceRole   { get; init; }   // "Capture" | "Render" | "Bidirectional"
    public string? DeviceType   { get; init; }   // "Camera" | "DepthCamera" | "Display" | "HMD" | "WearableCamera" | "Other"
    public string? Manufacturer { get; init; }
    public string? Model        { get; init; }
    public object? DeviceGeometry { get; init; }  // DeviceSceneGeometry
    public VisualOptics?               Optics               { get; init; }
    public VisualCaptureConfiguration? CaptureConfiguration { get; init; }
    public VisualRenderConfiguration?  RenderConfiguration  { get; init; }
    public VisualOperationalParameters? OperationalParameters { get; init; }
    public VisualSynchronisation?      Synchronisation      { get; init; }
}

public sealed class VisualOptics
{
    public double? FieldOfView { get; init; }
    public double? FocalLength { get; init; }
    public double? Aperture    { get; init; }
}

public sealed class VisualCaptureConfiguration
{
    public string? Resolution   { get; init; }   // "1920x1080"
    public double? FrameRate    { get; init; }
    public string? SamplingMode { get; init; }   // "Progressive" | "Interlaced"
}

public sealed class VisualRenderConfiguration
{
    public string? Resolution  { get; init; }
    public string? DisplayType { get; init; }   // "LCD" | "OLED" | "MicroLED" | "Projection" | "HMD" | "Other"
    public double? RefreshRate { get; init; }
}

public sealed class VisualOperationalParameters
{
    public double? Brightness   { get; init; }
    public double? Contrast     { get; init; }
    public double? DynamicRange { get; init; }
}

public sealed class VisualSynchronisation
{
    public string? ClockType { get; init; }   // "Internal" | "External" | "NetworkSynchronised"
    public string? Reference { get; init; }
}

// Ã¢â€â‚¬Ã¢â€â‚¬ Format enums (TFA/V1.5/formats/*) Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬

// TFA/V1.5/formats/Visual2DStaticFormats.json
public enum Visual2DStaticFormat
{
    BMP, BoundingBox, JPEG, JPEG2000, JPEGXS, PNG, RAW, SVG, TIFF
}

// TFA/V1.5/formats/VisualFileFormats.json
public enum VisualFileFormat
{
    AVI, EXIF, JPEGXS, MP4, MOV, MKV, WEBM, FLV, ThreeGP, TIFF
}

// TFA/V1.5/formats/ColourFormats.json
public enum ColourFormat
{
    ACES2065_1, ACEScg, BT_601, BT_709, BT_2020, BT_2100_PQ, BT_2100_HLG,
    DCI_P3, SMPTE_170M, SMPTE_240M, SMPTE_2036_1, SMPTE_2084, SMPTE_2086
}

// Maps a file extension to the TFA Visual 2D static content format.
public static class VisualFormatDetection
{
    public static Visual2DStaticFormat? FromExtension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".bmp"                   => Visual2DStaticFormat.BMP,
            ".jpg" or ".jpeg"        => Visual2DStaticFormat.JPEG,
            ".jp2" or ".j2k"         => Visual2DStaticFormat.JPEG2000,
            ".png"                   => Visual2DStaticFormat.PNG,
            ".raw"                   => Visual2DStaticFormat.RAW,
            ".svg"                   => Visual2DStaticFormat.SVG,
            ".tif" or ".tiff"        => Visual2DStaticFormat.TIFF,
            _                        => null
        };
    }
}
