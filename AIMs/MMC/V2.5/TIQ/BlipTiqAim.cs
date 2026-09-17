using System;
using System.Threading.Tasks;

using Mpai.Core;

namespace Mpai.Mmc.Tiq;

public sealed class BlipTiqConfiguration
{
    public required string VisionModel { get; init; }
    public required string EncoderModel { get; init; }
    public required string DecoderModel { get; init; }
    public required string VocabFile { get; init; }
}

// ---------------------------------------------------------------------------
//  MMC-TIQ worked transform:
//    Basic Text Object (question) + Basic Visual Object -> Basic Text Object (answer)
//    inherit   : Language from the question's Text Qualifier (answer is in that language)
//    determine : text Format = UTF-8 (BLIP emits plain text)
//  Wraps the real BLIP pipeline (TIQEngine); replaces the earlier BLIP stub.
// ---------------------------------------------------------------------------
public sealed class BlipTiqAim : ITiqAim, IDisposable
{
    private readonly TIQEngine _engine;

    public BlipTiqAim(BlipTiqConfiguration c)
        => _engine = new TIQEngine(c.VisionModel, c.EncoderModel, c.DecoderModel, c.VocabFile);

    public Task<BasicTextObject> ProcessAsync(BasicTextObject question, BasicVisualObject image)
    {
        if (image.Data.Length > 0)
            _engine.SetImageFromBytes(image.Data);
        else
            _engine.SetImage(image.FileName
                ?? throw new ArgumentException("Visual object has neither inline data nor a file name."));

        var answer = _engine.Ask(question.GetText());

        return Task.FromResult(BasicTextObject.FromText(answer, BuildTextQualifier(question)));
    }

    private static TextQualifier BuildTextQualifier(BasicTextObject question)
    {
        // inherit Language from the question (TIQ does not detect language itself).
        Language? language = question.TextQualifier?.Attributes?.Language;

        return new TextQualifier
        {
            TextQualifierID = Guid.NewGuid().ToString(),
            Format = new TextFormat
            {
                ContentFormat = new TextContentFormat { Static = TextStaticFormat.Utf8 }   // determine
            },
            Attributes = new TextAttributes { Language = language }
        };
    }

    public void Dispose() => _engine.Dispose();
}
