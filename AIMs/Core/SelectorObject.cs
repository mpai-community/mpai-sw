namespace Mpai.Core;

// OSD-SEL-V1.5 â€” Selector.
//
// One Selector Data Type, used for two different jobs in MMC-TST, told apart by
// PortNumber rather than by having two types:
//
//   PairLanguageSelector  (PortNumber 1) â€” the input and output language codes.
//   TextSpeechSelector    (PortNumber 2) â€” which of Input Text and Recognised
//                                          Text to translate when BOTH arrive.
//
// Fields not relevant to a given instance are left null, and the reader takes
// only what it needs. InputLanguage may be null even on the pair selector: the
// Speech Qualifier of the input Speech Object already carries it, and Automatic
// Speech Recognition reads it from there.
public sealed class BasicSelectorObject
{
    public string Header { get; init; } = "OSD-SEL-V1.5";

    // ISO 639-1 or BCP-47.
    public string? InputLanguage  { get; init; }
    public string? OutputLanguage { get; init; }

    // Which text to translate when Input Text and Recognised Text are both
    // present. Null means "whichever arrived"; supplying it when only one
    // arrived is harmless and ignored.
    public TextSource? TranslateFrom { get; init; }

    public static BasicSelectorObject Languages(string? from, string to) => new()
    {
        InputLanguage  = from,
        OutputLanguage = to
    };

    public static BasicSelectorObject Source(TextSource source) => new()
    {
        TranslateFrom = source
    };
}

public enum TextSource
{
    InputText,
    RecognisedText
}