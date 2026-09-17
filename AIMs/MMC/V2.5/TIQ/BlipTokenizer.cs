using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

public class BlipTokenizer
{
    private readonly Dictionary<long, string> idToToken = new();
    private readonly Dictionary<string, long> tokenToId = new();

    private readonly long unkId;

    public BlipTokenizer(string vocabFile)
    {
        long id = 0;

        foreach (var line in File.ReadLines(vocabFile))
        {
            // vocab.txt is one token per line; do NOT Trim (a token could be
            // whitespace), but strip the trailing newline only.
            var token = line.TrimEnd('\r', '\n');

            idToToken[id] = token;
            tokenToId[token] = id;
            id++;
        }

        unkId = tokenToId.TryGetValue("[UNK]", out var u) ? u : 100;
    }

    public string DecodeToken(long tokenId)
    {
        return idToToken.TryGetValue(tokenId, out var token)
            ? token
            : $"[{tokenId}]";
    }

    // Reassembles tokens into text, gluing WordPiece continuations (##xxx)
    // onto the preceding word with no space:  ra + ##cco + ##on -> raccoon
    public string Decode(IEnumerable<long> tokenIds)
    {
        var sb = new StringBuilder();

        foreach (var tokenId in tokenIds)
        {
            if (!idToToken.TryGetValue(tokenId, out var token))
            {
                continue;
            }

            if (token is "[CLS]" or "[SEP]" or "[PAD]")
            {
                continue;
            }

            if (token.StartsWith("##", StringComparison.Ordinal))
            {
                sb.Append(token.AsSpan(2));       // continuation: no space
            }
            else
            {
                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }
                sb.Append(token);
            }
        }

        return sb.ToString();
    }

    // Full BERT-style tokenization: lower-case + strip accents, split on
    // whitespace and punctuation, then greedy WordPiece against the vocab.
    public long[] Encode(string text)
    {
        var ids = new List<long> { GetId("[CLS]") };

        foreach (var word in BasicTokenize(text))
        {
            foreach (var piece in WordPiece(word))
            {
                ids.Add(GetId(piece));
            }
        }

        ids.Add(GetId("[SEP]"));
        return ids.ToArray();
    }

    private long GetId(string token)
    {
        return tokenToId.TryGetValue(token, out var id) ? id : unkId;
    }

    // ---- BERT basic tokenizer: lower-case, strip accents, split punctuation.
    private static IEnumerable<string> BasicTokenize(string text)
    {
        text = StripAccents(text.ToLowerInvariant());

        var output = new List<string>();

        foreach (var chunk in text.Split(
                     (char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var current = new StringBuilder();

            foreach (var ch in chunk)
            {
                if (IsPunctuation(ch))
                {
                    if (current.Length > 0)
                    {
                        output.Add(current.ToString());
                        current.Clear();
                    }
                    output.Add(ch.ToString());     // each punct is its own token
                }
                else
                {
                    current.Append(ch);
                }
            }

            if (current.Length > 0)
            {
                output.Add(current.ToString());
            }
        }

        return output;
    }

    // ---- Greedy longest-match-first WordPiece.
    private IEnumerable<string> WordPiece(string word)
    {
        const int maxCharsPerWord = 200;

        if (word.Length > maxCharsPerWord)
        {
            return new[] { "[UNK]" };
        }

        var pieces = new List<string>();
        int start = 0;

        while (start < word.Length)
        {
            int end = word.Length;
            string? match = null;

            while (start < end)
            {
                var sub = word.Substring(start, end - start);
                if (start > 0)
                {
                    sub = "##" + sub;
                }

                if (tokenToId.ContainsKey(sub))
                {
                    match = sub;
                    break;
                }

                end--;
            }

            if (match is null)
            {
                return new[] { "[UNK]" };     // any failure -> whole word is UNK
            }

            pieces.Add(match);
            start = end;
        }

        return pieces;
    }

    private static string StripAccents(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);

        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(ch);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static bool IsPunctuation(char c)
    {
        // BERT treats all ASCII non-alphanumeric as punctuation, plus any
        // Unicode punctuation category.
        if ((c >= 33 && c <= 47) || (c >= 58 && c <= 64) ||
            (c >= 91 && c <= 96) || (c >= 123 && c <= 126))
        {
            return true;
        }

        return char.IsPunctuation(c);
    }
}
