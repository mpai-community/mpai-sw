using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using Mpai.Core;

namespace Mmc.Ttt.Onnx;

// MMC-TTT-V2.5 engine: M2M-100 418M under ONNX Runtime.
//
// Written against the graph metadata rather than assumption (--graphtest). The
// UNMERGED decoder pair is used: the merged export failed on the first step
// inside its own optimum::if, and the pair has no If node, no use_cache_branch
// and no zero-length tensors - the three things that failure involved.
//
//   encoder                 input_ids, attention_mask (int64 [b, enc])
//                        -> last_hidden_state [b, enc, 1024]
//
//   decoder_model           input_ids [b, dec], encoder_hidden_states,
//                           encoder_attention_mask
//                        -> logits [b, dec, 128112]
//                           present.L.decoder.{key,value}
//                           present.L.encoder.{key,value}
//
//   decoder_with_past_model input_ids [b, 1], encoder_attention_mask,
//                           past_key_values.L.{decoder,encoder}.{key,value}
//                        -> logits [b, 1, 128112]
//                           present.L.decoder.{key,value}   <- DECODER ONLY
//
// Two consequences of that last line, and both are easy to get wrong:
//
//   * the with-past model does NOT take encoder_hidden_states - the encoder
//     key/values reach it through the past instead;
//   * it does NOT return the encoder present either. So the encoder past is
//     captured ONCE from the first step and passed unchanged for ever after.
//     Feeding back only what this model returns would leave the encoder past
//     missing from the third step onward.
//
// input_ids is fixed at [b, 1] there: exactly one token per step.
//
// 12 layers, 16 heads, 64 per head. Logit width 128112 leaves room above the
// 128104 real tokens, so the language ids sit safely inside it.
//
// Generation follows M2M-100's own arrangement: the decoder STARTS with </s>
// and the target language token comes first after it. Step one feeds
// [</s>, __tgt__] and reads the last position's logits, which is forced-token
// generation without the machinery.
public sealed class OnnxTttAim : ITttAim, IDisposable
{
    private readonly OnnxTttConfiguration _configuration;
    private readonly M2M100Tokeniser      _tokeniser;
    private readonly InferenceSession     _encoder;
    private readonly InferenceSession     _decoderFirst;
    private readonly InferenceSession     _decoderPast;
    private readonly SemaphoreSlim        _oneAtATime = new(1, 1);

    public OnnxTttAim(OnnxTttConfiguration configuration)
    {
        _configuration = configuration;

        _tokeniser = M2M100Tokeniser.Load(
            configuration.SpmModelPath,
            configuration.VocabPath,
            configuration.SpecialTokensPath);

        _encoder      = new InferenceSession(configuration.EncoderModelPath);
        _decoderFirst = new InferenceSession(configuration.DecoderFirstModelPath);
        _decoderPast  = new InferenceSession(configuration.DecoderPastModelPath);
    }

    public async Task<BasicTextObject> ProcessAsync(
        BasicTextObject     text,
        BasicSelectorObject languages,
        CancellationToken   token = default)
    {
        var target = Primary(languages.OutputLanguage);
        if (target.Length == 0)
            throw new InvalidOperationException(
                "MMC-TTT-V2.5: the Pair Language Selector carries no output language.");

        // The source language: the Selector if it says, else the Text
        // Qualifier, which Automatic Speech Recognition stamps. This is why
        // MMC-TST has no Input Language Selector.
        var source = Primary(languages.InputLanguage);
        if (source.Length == 0)
            source = Primary(text.TextQualifier?.Attributes?.Language?.LanguageCode);
        if (source.Length == 0)
            source = "en";

        if (_tokeniser.LanguageTokenId(source) is null)
            throw new InvalidOperationException($"MMC-TTT-V2.5: unknown source language '{source}'.");
        if (_tokeniser.LanguageTokenId(target) is not int targetId)
            throw new InvalidOperationException($"MMC-TTT-V2.5: unknown output language '{target}'.");

        var sourceText = text.GetText();

        // The sessions hold mutable state only per call, but one ONNX session
        // running two translations at once is not worth the risk here.
        await _oneAtATime.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var translated = await Task.Run(
                () => Translate(sourceText, source, targetId, token), token).ConfigureAwait(false);

            return BasicTextObject.FromText(
                translated,
                BuildTextQualifier(text, target));
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private string Translate(string sourceText, string sourceLanguage, int targetId, CancellationToken token)
    {
        var inputIds = _tokeniser.Encode(sourceText, sourceLanguage);

        if (inputIds.Length > _configuration.MaxInputTokens)
        {
            // Truncating silently would mistranslate the tail into nothing.
            Console.WriteLine(
                $"[MMC-TTT-V2.5] input is {inputIds.Length} tokens; truncating to " +
                $"{_configuration.MaxInputTokens}. The tail will be lost.");

            inputIds = inputIds.Take(_configuration.MaxInputTokens - 1)
                               .Append(_tokeniser.EosId)
                               .ToArray();
        }

        var encoderLength = inputIds.Length;

        // ---- encoder -------------------------------------------------------
        var encoderInputs = new List<NamedOnnxValue>
        {
            Int64("input_ids", inputIds, 1, encoderLength),
            Int64("attention_mask", Enumerable.Repeat(1L, encoderLength).ToArray(), 1, encoderLength)
        };

        float[] encoderHidden;
        int hiddenSize;
        using (var encoded = _encoder.Run(encoderInputs))
        {
            var hidden = encoded.First(value => value.Name == "last_hidden_state")
                                .AsTensor<float>();
            hiddenSize    = hidden.Dimensions[2];
            encoderHidden = hidden.ToArray();
        }

        // ---- decoder, first step ------------------------------------------
        // [</s>, __tgt__] with no past at all; the last position's logits give
        // the first real token.
        var generated = new List<int> { _tokeniser.EosId, targetId };
        var attention = Enumerable.Repeat(1L, encoderLength).ToArray();

        Dictionary<string, PastTensor> decoderPast;
        Dictionary<string, PastTensor> encoderPast;
        int next;

        var firstInputs = new List<NamedOnnxValue>
        {
            Int64("input_ids", generated.ToArray(), 1, generated.Count),
            NamedOnnxValue.CreateFromTensor(
                "encoder_hidden_states",
                new DenseTensor<float>(encoderHidden, new[] { 1, encoderLength, hiddenSize })),
            Int64("encoder_attention_mask", attention, 1, encoderLength)
        };

        using (var decoded = _decoderFirst.Run(firstInputs))
        {
            next        = LastPositionArgMax(decoded);
            decoderPast = CapturePresent(decoded, "decoder");
            encoderPast = CapturePresent(decoded, "encoder");
        }

        // ---- decoder, remaining steps -------------------------------------
        for (var step = 0; step < _configuration.MaxOutputTokens; step++)
        {
            if (next == _tokeniser.EosId) break;

            generated.Add(next);
            token.ThrowIfCancellationRequested();

            var inputs = new List<NamedOnnxValue>
            {
                Int64("input_ids", new[] { (long)next }, 1, 1),
                Int64("encoder_attention_mask", attention, 1, encoderLength)
            };

            // The evolving decoder past, plus the encoder past held from step one:
            // this model returns only the former, so the latter must be re-supplied.
            foreach (var entry in decoderPast.Concat(encoderPast))
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor(
                    entry.Key,
                    new DenseTensor<float>(entry.Value.Data, entry.Value.Dimensions)));
            }

            using var decoded = _decoderPast.Run(inputs);
            next        = LastPositionArgMax(decoded);
            decoderPast = CapturePresent(decoded, "decoder");
        }

        // Decode() drops the leading </s> and language token.
        return _tokeniser.Decode(generated);
    }

    // The chosen token at the final position of the logits.
    private static int LastPositionArgMax(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> decoded)
    {
        var logits = decoded.First(value => value.Name == "logits").AsTensor<float>();

        var positions = logits.Dimensions[1];
        var width     = logits.Dimensions[2];

        return ArgMax(logits, (positions - 1) * width, width);
    }

    private static int ArgMax(Tensor<float> logits, int offset, int width)
    {
        var best      = 0;
        var bestValue = float.NegativeInfinity;

        var flat = logits.ToArray();
        for (var index = 0; index < width; index++)
        {
            var value = flat[offset + index];
            if (value > bestValue)
            {
                bestValue = value;
                best      = index;
            }
        }

        return best;
    }

    private sealed record PastTensor(float[] Data, int[] Dimensions);

    // present.L.SIDE.key/value  ->  past_key_values.L.SIDE.key/value
    //
    // The tensors must be COPIED: the results collection is disposed when the
    // using block ends, and its buffers go with it.
    private static Dictionary<string, PastTensor> CapturePresent(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> decoded,
        string side)
    {
        var past   = new Dictionary<string, PastTensor>(StringComparer.Ordinal);
        var marker = "." + side + ".";

        foreach (var value in decoded)
        {
            if (!value.Name.StartsWith("present.", StringComparison.Ordinal)) continue;
            if (!value.Name.Contains(marker, StringComparison.Ordinal)) continue;

            var tensor = value.AsTensor<float>();
            past["past_key_values." + value.Name.Substring("present.".Length)] =
                new PastTensor(tensor.ToArray(), tensor.Dimensions.ToArray());
        }

        return past;
    }

    private static NamedOnnxValue Int64(string name, long[] data, int rows, int columns) =>
        NamedOnnxValue.CreateFromTensor(
            name,
            new DenseTensor<long>(data, new[] { rows, columns }));

    private static NamedOnnxValue Int64(string name, int[] data, int rows, int columns) =>
        Int64(name, data.Select(value => (long)value).ToArray(), rows, columns);

    // inherit Format; DETERMINE the output language, which only this AIM knows
    // and which MMC-TTS reads to choose its voice.
    private static TextQualifier BuildTextQualifier(BasicTextObject source, string outputLanguage) =>
        new()
        {
            TextQualifierID = Guid.NewGuid().ToString(),
            Format = source.TextQualifier?.Format
                     ?? new TextFormat
                        {
                            ContentFormat = new TextContentFormat { Static = TextStaticFormat.Utf8 }
                        },
            Attributes = new TextAttributes
            {
                Language = new Language
                {
                    LanguageCode   = outputLanguage,
                    LanguageFormat = LanguageFormat.Iso639_1
                }
            }
        };

    private static string Primary(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode)) return string.Empty;
        var head = languageCode.Trim().ToLowerInvariant().Split('-', '_')[0];
        return head.Length <= 2 ? head : head.Substring(0, 2);
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _decoderFirst.Dispose();
        _decoderPast.Dispose();
        _oneAtATime.Dispose();
    }
}

public sealed class OnnxTttConfiguration
{
    public required string  EncoderModelPath      { get; init; }
    public required string  DecoderFirstModelPath { get; init; }
    public required string  DecoderPastModelPath  { get; init; }
    public required string  SpmModelPath      { get; init; }
    public required string  VocabPath         { get; init; }
    public          string? SpecialTokensPath { get; init; }

    public int MaxInputTokens  { get; init; } = 256;
    public int MaxOutputTokens { get; init; } = 256;
}