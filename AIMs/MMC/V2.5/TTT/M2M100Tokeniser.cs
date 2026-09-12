using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using Microsoft.ML.Tokenizers;

namespace Mmc.Ttt.Onnx;

// Tokenisation for M2M-100.
//
// M2M-100 is MIT-licensed and covers 100 languages named by plain ISO 639-1
// codes - "en", "it" - which is exactly what the Pair Language Selector and the
// Text Qualifier already carry. (NLLB-200 would have won on coverage but is
// CC-BY-NC, unusable in BSD-3-Clause Reference Software.)
//
// Three files, all small:
//   sentencepiece.bpe.model   the SentencePiece model, read by ML.Tokenizers
//   vocab.json                the fairseq dictionary, token -> id
//   special_tokens_map.json   the ORDERED language list (optional; see below)
//
// Two things about this model are easy to get wrong and silent when wrong:
//
// 1. The language tokens are NOT in vocab.json. Hugging Face appends them after
//    it, so id("__it__") = vocab.json entry count + the language's INDEX in a
//    fixed list. The order of that list is therefore load-bearing: shift it by
//    one and the model translates fluently into the wrong language. The order is
//    read from special_tokens_map.json when present, and only falls back to the
//    list embedded below when it is not.
//
// 2. The encoder input is [__source__] pieces </s> - the SOURCE language token
//    leads the encoder. The TARGET language token leads the decoder instead, as
//    its first forced token, after </s> as the decoder start.
public sealed class M2M100Tokeniser
{
    // The 100 M2M-100 language codes IN ORDER. Only used when
    // special_tokens_map.json is unavailable; the order is what matters.
    private static readonly string[] FallbackLanguages =
    {
        "af","am","ar","ast","az","ba","be","bg","bn","br","bs","ca","ceb","cs","cy",
        "da","de","el","en","es","et","fa","ff","fi","fr","fy","ga","gd","gl","gu",
        "ha","he","hi","hr","ht","hu","hy","id","ig","ilo","is","it","ja","jv","ka",
        "kk","km","kn","ko","lb","lg","ln","lo","lt","lv","mg","mk","ml","mn","mr",
        "ms","my","ne","nl","no","ns","oc","or","pa","pl","ps","pt","ro","ru","sd",
        "si","sk","sl","so","sq","sr","ss","su","sv","sw","ta","th","tl","tn","tr",
        "uk","ur","uz","vi","wo","xh","yi","yo","zh","zu"
    };

    private readonly SentencePieceTokenizer  _pieces;
    private readonly Dictionary<string, int> _tokenToId;
    private readonly Dictionary<int, string> _idToToken;
    private readonly Dictionary<string, int> _languageToId;

    public int VocabularyEntries { get; }
    public int LanguageCount     => _languageToId.Count;
    public bool LanguagesFromFile { get; }

    public int UnknownId { get; }
    public int EosId     { get; }
    public int PadId     { get; }
    public int BosId     { get; }

    private M2M100Tokeniser(
        SentencePieceTokenizer pieces,
        Dictionary<string, int> tokenToId,
        IReadOnlyList<string> languages,
        bool languagesFromFile)
    {
        _pieces    = pieces;
        _tokenToId = tokenToId;
        _idToToken = tokenToId.GroupBy(pair => pair.Value)
                              .ToDictionary(group => group.Key, group => group.First().Key);

        VocabularyEntries = tokenToId.Count;
        LanguagesFromFile = languagesFromFile;

        // id("__xx__") = vocabulary size + index in the ordered language list.
        _languageToId = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < languages.Count; index++)
            _languageToId[languages[index]] = VocabularyEntries + index;

        UnknownId = Id("<unk>") ?? 3;
        EosId     = Id("</s>")  ?? 2;
        PadId     = Id("<pad>") ?? 1;
        BosId     = Id("<s>")   ?? 0;
    }

    public static M2M100Tokeniser Load(
        string spmModelPath,
        string vocabJsonPath,
        string? specialTokensMapPath = null)
    {
        using var stream = File.OpenRead(spmModelPath);

        // Positional: the parameter names differ between ML.Tokenizers versions.
        // false, false = add neither begin- nor end-of-sentence; this class adds
        // the language token and </s> itself, because M2M-100's arrangement is
        // its own.
        var pieces = SentencePieceTokenizer.Create(stream, false, false);

        using var vocabDocument = JsonDocument.Parse(File.ReadAllText(vocabJsonPath));

        var tokenToId = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in vocabDocument.RootElement.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.Number)
                tokenToId[entry.Name] = entry.Value.GetInt32();
        }

        var languages        = ReadLanguages(specialTokensMapPath);
        var fromFile         = languages is not null;
        var orderedLanguages = languages ?? FallbackLanguages;

        return new M2M100Tokeniser(pieces, tokenToId, orderedLanguages, fromFile);
    }

    // additional_special_tokens holds "__af__", "__am__", ... in the order the
    // ids were assigned. Order is the whole point of reading this file.
    private static string[]? ReadLanguages(string? specialTokensMapPath)
    {
        if (string.IsNullOrWhiteSpace(specialTokensMapPath) ||
            !File.Exists(specialTokensMapPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(specialTokensMapPath));
            if (!document.RootElement.TryGetProperty("additional_special_tokens", out var tokens) ||
                tokens.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var codes = new List<string>();
            foreach (var token in tokens.EnumerateArray())
            {
                var text = token.ValueKind == JsonValueKind.String
                    ? token.GetString()
                    : token.TryGetProperty("content", out var content) ? content.GetString() : null;

                if (string.IsNullOrWhiteSpace(text)) continue;
                if (!text.StartsWith("__", StringComparison.Ordinal) ||
                    !text.EndsWith("__", StringComparison.Ordinal)) continue;

                codes.Add(text.Substring(2, text.Length - 4));
            }

            return codes.Count > 0 ? codes.ToArray() : null;
        }
        catch
        {
            return null;   // fall back to the embedded order
        }
    }

    public int? Id(string token) =>
        _tokenToId.TryGetValue(token, out var id) ? id : null;

    public int? LanguageTokenId(string languageCode) =>
        _languageToId.TryGetValue(languageCode, out var id) ? id : null;

    public IReadOnlyList<string> Pieces(string text) =>
        _pieces.EncodeToTokens(text, out _).Select(token => token.Value).ToList();

    // Encoder input: [__source__] pieces </s>
    public int[] Encode(string text, string? sourceLanguage)
    {
        var ids = new List<int>();

        if (!string.IsNullOrWhiteSpace(sourceLanguage) &&
            LanguageTokenId(sourceLanguage) is int sourceId)
        {
            ids.Add(sourceId);
        }

        foreach (var piece in Pieces(text))
            ids.Add(Id(piece) ?? UnknownId);

        ids.Add(EosId);
        return ids.ToArray();
    }

    // Drop control and language tokens; U+2581 marks a word boundary.
    public string Decode(IEnumerable<int> ids)
    {
        var pieces = new List<string>();

        foreach (var id in ids)
        {
            if (id >= VocabularyEntries) continue;          // a language token
            if (!_idToToken.TryGetValue(id, out var token)) continue;
            if (token is "<s>" or "</s>" or "<pad>" or "<unk>") continue;
            pieces.Add(token);
        }

        var text = string.Concat(pieces).Replace('\u2581', ' ').Trim();

        return SeparateSentences(text);
    }

    // A space after sentence-final punctuation, where SentencePiece left none.
    //
    // Concatenation is faithful: a space appears only where the model marked one
    // with U+2581. After a full stop the next word often carries NO marker, so
    // the pieces join as "disperare.Vedi" - nothing is dropped, the boundary was
    // never there to drop.
    //
    // On the page that is a typographic blemish. Spoken it is worse: Piper reads
    // "disperare.Vedi" as ONE token, so two sentences run together and the full
    // stop is not heard at all. That is why this belongs here and not in the
    // speech AIM - the TEXT is wrong, and everything downstream inherits it,
    // including what the reader sees.
    //
    // A decimal point is not a sentence end, so digits on both sides are left
    // alone: "3.14" must not become "3. 14". An ellipsis is not three sentence
    // ends, so a run of stops counts once. Closing quotes and brackets belong to
    // the sentence just finished, so they are skipped - which does leave
    // \"citata.\"Poi joined, rare enough to prefer over splitting inside a
    // quotation.
    private static string SeparateSentences(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var separated = new System.Text.StringBuilder(text.Length + 8);

        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            separated.Append(current);

            if (index + 1 >= text.Length) continue;

            // Commas, semicolons and colons too, not only sentence ends.
            //
            // The first version covered . ! ? and left "anno,caccia" and
            // "miniera,c'e" joined - the same missing boundary at a different
            // mark. A comma with no space after it is a typographic error in the
            // text whatever follows, so it belongs here for the same reason.
            if (current is not ('.' or '!' or '?' or ',' or ';' or ':')) continue;

            var next = text[index + 1];

            if (char.IsWhiteSpace(next)) continue;
            if (next is '.' or '!' or '?' or ',' or ';' or ':' or ')' or ']' or '}' or '"' or '\'' or '\u00bb' or '\u201d') continue;

            // Digits on both sides: a decimal point, a thousands separator, a
            // time. "3.14", "1,000" and "12:30" must survive intact - which is
            // why the test is on the NEIGHBOURS and not on the mark.
            if (char.IsDigit(next) && index > 0 && char.IsDigit(text[index - 1])) continue;

            separated.Append(' ');
        }

        return separated.ToString();
    }

    public int UnknownCount(string text) =>
        Pieces(text).Count(piece => Id(piece) is null);
}