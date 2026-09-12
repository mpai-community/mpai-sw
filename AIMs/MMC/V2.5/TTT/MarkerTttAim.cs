using Mpai.Core;

namespace Mmc.Ttt;

// A translation engine that does not translate.
//
// It marks the text with the target language and hands it back, so the TST
// topology - port ordinals, the skip path, ASR and TTS either side - can be
// exercised before a 2.4 GB NLLB model is on disk. Never mistake its output for
// a translation: that is why the marker is loud rather than silent.
//
// It does, however, stamp the OUTPUT language on the Text Qualifier it returns,
// because that is not decoration - it is the entire language handoff to
// Text-to-Speech, which chooses its voice from it. An earlier version returned
// the text with no Qualifier at all, and MMC-TTS quietly spoke Italian in an
// American English voice with nothing in the log to show it.
public sealed class MarkerTttAim : ITttAim
{
    public Task<BasicTextObject> ProcessAsync(
        BasicTextObject     text,
        BasicSelectorObject languages,
        CancellationToken   token = default)
    {
        var target =
            string.IsNullOrWhiteSpace(languages.OutputLanguage)
                ? "??"
                : languages.OutputLanguage;

        var marked =
            $"[{target} NOT-TRANSLATED] {text.GetText()}";

        return Task.FromResult(
            BasicTextObject.FromText(
                marked,
                BuildTextQualifier(text, target)));
    }

    // inherit    : Format from the source text, since translation does not
    //              change the encoding
    // determine  : Language - the output language is what THIS AIM knows and
    //              nothing downstream can infer
    private static TextQualifier BuildTextQualifier(
        BasicTextObject source,
        string outputLanguage)
    {
        var format =
            source.TextQualifier?.Format
            ?? new TextFormat
               {
                   ContentFormat = new TextContentFormat { Static = TextStaticFormat.Utf8 }
               };

        return new TextQualifier
        {
            TextQualifierID = Guid.NewGuid().ToString(),
            Format          = format,
            Attributes      = new TextAttributes
            {
                Language = new Language
                {
                    LanguageCode   = outputLanguage,
                    LanguageFormat = LanguageFormat.Iso639_1
                }
            }
        };
    }
}