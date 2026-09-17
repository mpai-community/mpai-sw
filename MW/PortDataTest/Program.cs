using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Mpai.Core;
using Mpai.Mas.PortData;

internal static class Program
{
    private static int failures;

    private static void Check(string what, object? expected, object? actual)
    {
        var ok = Equals(expected?.ToString(), actual?.ToString());
        if (!ok) failures++;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}: expected [{expected}] got [{actual}]");
    }

    private static int Main()
    {
        var codecs = PortDataCodecs.Default();

        // ---- OSD-BSO-V1.5 ---------------------------------------------------
        Console.WriteLine("OSD-BSO-V1.5");
        var speech = new BasicSpeechObject
        {
            BasicSpeechObjectID = "bso-1",
            Data                = new byte[] { 1, 2, 3, 4, 250 },
            SpeechQualifier     = new SpeechQualifier
            {
                SpeechQualifierID = "spq-1",
                Format = new SpeechFormat
                {
                    ContentFormats   = new SpeechContentFormats
                        { RawData = new Pcm { SamplingFrequency = 16000, Precision = 16 } },
                    TransportFormats = new SpeechTransportFormats
                        { FileFormat = SpeechFileFormat.Wav }
                },
                Attributes = new SpeechAttributes
                {
                    Source   = SpeechSource.Real,
                    Metadata = new SpeechMetadata
                    {
                        Language = new Language { LanguageCode = "it" }
                    },
                    Device = new AudioDevice
                    {
                        DeviceType           = "Microphone",
                        CaptureConfiguration = new CaptureConfiguration { ChannelCount = 1 }
                    }
                }
            }
        };

        var bsoCodec = codecs.For("OSD-BSO-V1.5");
        var backSpeech = MpaiJson.FromJson<BasicSpeechObject>(
            bsoCodec.ToInternal(bsoCodec.ToWire(MpaiJson.ToJson(speech))));

        Check("data length",   5, backSpeech.Data.Length);
        Check("sampling freq", 16000d, backSpeech.SpeechQualifier?.Format?.ContentFormats?.RawData?.SamplingFrequency);
        Check("precision",     16, backSpeech.SpeechQualifier?.Format?.ContentFormats?.RawData?.Precision);
        Check("language",      "it", backSpeech.SpeechQualifier?.Attributes?.Metadata?.Language?.LanguageCode);
        Check("channels",      1, backSpeech.SpeechQualifier?.Attributes?.Device?.CaptureConfiguration?.ChannelCount);

        // ---- OSD-BTO-V1.5 ---------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("OSD-BTO-V1.5");
        var text = BasicTextObject.FromText(
            "what animal is this?",
            new TextQualifier
            {
                TextQualifierID = "txq-1",
                Format     = new TextFormat
                    { ContentFormat = new TextContentFormat { Static = TextStaticFormat.Utf8 } },
                Attributes = new TextAttributes
                    { Language = new Language { LanguageCode = "en" } }
            });

        var btoCodec = codecs.For("OSD-BTO-V1.5");
        var backText = MpaiJson.FromJson<BasicTextObject>(
            btoCodec.ToInternal(btoCodec.ToWire(MpaiJson.ToJson(text))));

        Check("text",         text.GetText(), backText.GetText());
        Check("qualifier ID", "txq-1", backText.TextQualifier?.TextQualifierID);
        Check("static fmt",   TextStaticFormat.Utf8, backText.TextQualifier?.Format?.ContentFormat?.Static);
        Check("language",     "en", backText.TextQualifier?.Attributes?.Language?.LanguageCode);

        // a reference item, which text carries and speech refuses
        var referenced = new BasicTextObject
        {
            BasicTextObjectID = "bto-2",
            BasicTextData     = new List<BasicTextDataItem>
                { new ReferencedData(1234, "https://example.org/t.txt") }
        };
        var backRef = MpaiJson.FromJson<BasicTextObject>(
            btoCodec.ToInternal(btoCodec.ToWire(MpaiJson.ToJson(referenced))));
        Check("reference URI",
            "https://example.org/t.txt",
            (backRef.BasicTextData[0] as ReferencedData)?.DataURI);
        Check("reference length", 1234L, (backRef.BasicTextData[0] as ReferencedData)?.Length);

        // ---- OSD-BVO-V1.5 ---------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("OSD-BVO-V1.5");
        var visual = new BasicVisualObject
        {
            BasicVisualObjectID = "bvo-1",
            FileName            = @"D:\BI\TestData\deer.png",
            Data                = new byte[] { 137, 80, 78, 71 },
            VisualQualifier     = new VisualQualifier
            {
                VisualQualifierID = "viq-1",
                SubType = new VisualSubType { ColourFormat = ColourFormat.BT_709 },
                Format  = new VisualFormat
                {
                    Content = new VisualContentFormat
                    {
                        TimeSampling = new VisualTimeSampling
                            { Precision = new[] { new BitsPerPixel { BitsPerPixelValue = 24 } } },
                        TwoD = new Visual2D { Static = Visual2DStaticFormat.PNG }
                    },
                    Transport = new VisualTransport { FileFormat = VisualFileFormat.ThreeGP }
                },
                Attributes = new VisualAttributes
                {
                    VisualObjectType = "Object",
                    Source           = new VisualSource { Real = "Raster" },
                    Device           = new VisualDevice
                    {
                        DeviceType           = "Camera",
                        CaptureConfiguration = new VisualCaptureConfiguration { Resolution = "1920x1080" }
                    }
                }
            }
        };

        var bvoCodec = codecs.For("OSD-BVO-V1.5");
        var bvoWire  = bvoCodec.ToWire(MpaiJson.ToJson(visual));

        // the two enum spellings C# cannot write
        var wireText = Encoding.UTF8.GetString(bvoWire);
        Check("colour on the wire", true, wireText.Contains("\"BT.709\""));
        Check("3GP on the wire",    true, wireText.Contains("\"3GP\""));

        var backVisual = MpaiJson.FromJson<BasicVisualObject>(bvoCodec.ToInternal(bvoWire));

        Check("data length",   4, backVisual.Data.Length);
        Check("colour format", ColourFormat.BT_709, backVisual.VisualQualifier?.SubType?.ColourFormat);
        Check("bits-per-pixel", 24, backVisual.VisualQualifier?.Format?.Content?.TimeSampling?.Precision?[0].BitsPerPixelValue);
        Check("2D static",     Visual2DStaticFormat.PNG, backVisual.VisualQualifier?.Format?.Content?.TwoD?.Static);
        Check("file format",   VisualFileFormat.ThreeGP, backVisual.VisualQualifier?.Format?.Transport?.FileFormat);
        Check("object type",   "Object", backVisual.VisualQualifier?.Attributes?.VisualObjectType);
        Check("resolution",    "1920x1080", backVisual.VisualQualifier?.Attributes?.Device?.CaptureConfiguration?.Resolution);
        Check("FileName does NOT cross", null, backVisual.FileName);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILED");
        return failures;
    }
}