using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

using Microsoft.ML.Tokenizers;

namespace Heliosoph.DatumV.Functions.Tokenization;

/// <summary>
/// <c>tokenizer.encode(text, tokenizer_json_path) → INT64[]</c>. Encodes
/// the input text to a flat token-id array using the BPE tokenizer described
/// by a HuggingFace <c>tokenizer.json</c>. Loads on first reference and
/// caches the tokenizer process-wide so batched calls don't pay the load
/// cost per row.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Special tokens.</strong> No BOS / EOS added — the result is the
/// raw segmentation of the input. Wrap explicitly when the model wants them.
/// </para>
/// <para>
/// <strong>Supported algorithms.</strong> BPE only in v1. Unigram /
/// WordPiece / SentencePiece raise a clear "not yet supported" error. For
/// models that ship as a separate <c>vocab.json + merges.txt</c> pair
/// without a <c>tokenizer.json</c>, use <c>tokenizer.encode_bpe</c>.
/// </para>
/// </remarks>
public sealed class TokenizerEncodeFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "encode";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Encoding;

    /// <inheritdoc />
    public static string Description =>
        "Encodes text to a token-id array using a HuggingFace tokenizer.json: " +
        "tokenizer.encode(text, tokenizer_json_path). Returns INT64[]. " +
        "Loads on first call and caches the tokenizer process-wide. " +
        "BPE algorithm only in v1 — non-BPE tokenizer.json throws a clear error. " +
        "Path resolution mirrors CREATE MODEL USING (file:// absolute or relative to ModelDirectory).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("text",                 DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("tokenizer_json_path",  DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Int64))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<TokenizerEncodeFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.Int64));
        }

        string text = args[0].AsString();
        string resolvedPath = TokenizerPath.ResolveAbsoluteOrCatalogRelative(
            args[1].AsString(), "tokenizer.encode", frame);

        BpeTokenizer tokenizer = TokenizerCache.GetFromTokenizerJson(resolvedPath);
        return new ValueTask<ValueRef>(TokenizerOps.EncodeToValueRef(tokenizer, text));
    }
}

/// <summary>
/// <c>tokenizer.encode_bpe(text, vocab_json_path, merges_path) → INT64[]</c>.
/// Variant of <see cref="TokenizerEncodeFunction"/> for models that ship the
/// classic <c>vocab.json</c> + <c>merges.txt</c> pair without a unified
/// <c>tokenizer.json</c>. Same caching + path-resolution rules.
/// </summary>
public sealed class TokenizerEncodeBpeFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "encode_bpe";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Encoding;

    /// <inheritdoc />
    public static string Description =>
        "Encodes text to a token-id array using a BPE tokenizer loaded from separate vocab.json + merges.txt files: " +
        "tokenizer.encode_bpe(text, vocab_path, merges_path). Returns INT64[]. " +
        "Use when the model ships the legacy file pair without a unified tokenizer.json. " +
        "Loads on first call and caches process-wide.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("text",        DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("vocab_path",  DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("merges_path", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Int64))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<TokenizerEncodeBpeFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.Int64));
        }

        string text = args[0].AsString();
        // Catalog-relative resolution mirrors decode_bpe so SQL model bodies
        // can name vocab/merges as sibling files of the USING ONNX (e.g.
        // 'florence-2-base-ft-fp16/vocab.json') without absolute paths.
        string resolvedVocab  = TokenizerPath.ResolveAbsoluteOrCatalogRelative(
            args[1].AsString(), "tokenizer.encode_bpe", frame);
        string resolvedMerges = TokenizerPath.ResolveAbsoluteOrCatalogRelative(
            args[2].AsString(), "tokenizer.encode_bpe", frame);

        BpeTokenizer tokenizer = TokenizerCache.GetFromVocabMerges(resolvedVocab, resolvedMerges);
        return new ValueTask<ValueRef>(TokenizerOps.EncodeToValueRef(tokenizer, text));
    }
}

/// <summary>
/// <c>tokenizer.decode(ids, tokenizer_json_path) → STRING</c>. Inverse of
/// <see cref="TokenizerEncodeFunction"/>: reconstructs the original text
/// (modulo lossy normalisation, if the tokenizer applies any) from a
/// token-id array.
/// </summary>
public sealed class TokenizerDecodeFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "decode";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Encoding;

    /// <inheritdoc />
    public static string Description =>
        "Decodes a token-id array back to text using a HuggingFace tokenizer.json: " +
        "tokenizer.decode(ids, tokenizer_json_path). Returns STRING. " +
        "Inverse of tokenizer.encode; shares the same loader + cache + BPE-only constraint.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("ids",                  DataKindMatcher.Exact(DataKind.Int64), IsArray: ArrayMatch.Array),
                new ParameterSpec("tokenizer_json_path",  DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<TokenizerDecodeFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.String));
        }

        string resolvedPath = TokenizerPath.ResolveAbsoluteOrCatalogRelative(
            args[1].AsString(), "tokenizer.decode", frame);

        BpeTokenizer tokenizer = TokenizerCache.GetFromTokenizerJson(resolvedPath);
        return new ValueTask<ValueRef>(TokenizerOps.DecodeToValueRef(tokenizer, args[0]));
    }
}

/// <summary>
/// <c>tokenizer.decode_bpe(ids, vocab_path, merges_path) → STRING</c>.
/// Inverse of <see cref="TokenizerEncodeBpeFunction"/>.
/// </summary>
public sealed class TokenizerDecodeBpeFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "decode_bpe";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Encoding;

    /// <inheritdoc />
    public static string Description =>
        "Decodes a token-id array back to text using a BPE tokenizer loaded from separate vocab.json + merges.txt files: " +
        "tokenizer.decode_bpe(ids, vocab_path, merges_path). Returns STRING.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("ids",         DataKindMatcher.Exact(DataKind.Int64), IsArray: ArrayMatch.Array),
                new ParameterSpec("vocab_path",  DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("merges_path", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<TokenizerDecodeBpeFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.String));
        }

        // Catalog-relative resolution so SQL model bodies can name vocab /
        // merges as files-next-to-the-onnx (e.g. 'vit-gpt2-image-captioning/vocab.json')
        // without having to know the absolute models-directory path. Falls
        // back to absolute when the path is already absolute.
        string resolvedVocab  = TokenizerPath.ResolveAbsoluteOrCatalogRelative(
            args[1].AsString(), "tokenizer.decode_bpe", frame);
        string resolvedMerges = TokenizerPath.ResolveAbsoluteOrCatalogRelative(
            args[2].AsString(), "tokenizer.decode_bpe", frame);

        BpeTokenizer tokenizer = TokenizerCache.GetFromVocabMerges(resolvedVocab, resolvedMerges);
        return new ValueTask<ValueRef>(TokenizerOps.DecodeToValueRef(tokenizer, args[0]));
    }
}

/// <summary>
/// <c>tokenizer.byte_level_decode(text) → STRING</c>. Reverses GPT-2 /
/// RoBERTa / BART byte-level BPE encoding mojibake so a token-decoded
/// string becomes plain UTF-8. Used as a post-processing step right
/// after <c>tokenizer.decode_bpe</c> for byte-level-BPE families.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why it's a separate step.</strong> The byte-level BPE encoder
/// remaps non-printable bytes (including ASCII space and newline) to
/// high-Unicode codepoints so they survive BPE merging without
/// ambiguity. <c>BpeTokenizer.Decode</c> turns token ids back
/// into characters but does NOT invert that remap — a literal <c>Ġ</c>
/// (U+0120, the encoded space) leaks into the output. Every GPT-2 /
/// RoBERTa / BART export needs this inversion to produce real text.
/// </para>
/// <para>
/// <strong>Why not fold it into <c>decode_bpe</c>.</strong> Non-byte-level
/// BPE tokenizers (rare, but possible) would have this inversion
/// silently corrupt their output. Keeping it as an explicit post-step
/// is also more discoverable in SQL — the chain
/// <c>byte_level_decode(decode_bpe(...))</c> reads as "decode token ids
/// then unescape byte-level encoding," which is what the operation
/// actually does.
/// </para>
/// </remarks>
public sealed class TokenizerByteLevelDecodeFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "byte_level_decode";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Encoding;

    /// <inheritdoc />
    public static string Description =>
        "Reverses GPT-2 / RoBERTa / BART byte-level BPE encoding mojibake "
        + "(Ġ → space, Ċ → newline, etc.) on a token-decoded string and "
        + "trims surrounding whitespace. Pair with tokenizer.decode_bpe to "
        + "get plain UTF-8 text out of any byte-level BPE model's decoder "
        + "output. Also strips the HuggingFace special-token markers "
        + "(<s>, </s>, <pad>, <unk>, <mask>) that BpeTokenizer.Decode "
        + "leaves verbatim — Florence-2 / BART emit <s> as the first "
        + "generated token, which would otherwise leak into the caption.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("text", DataKindMatcher.Exact(DataKind.String), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<TokenizerByteLevelDecodeFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.String));
        }
        string raw = args[0].AsString();
        string decoded = Heliosoph.DatumV.Models.Onnx.ByteLevelBpeDecoder.Decode(raw).Trim();
        return new ValueTask<ValueRef>(ValueRef.FromString(decoded));
    }
}

/// <summary>
/// <c>tokenizer.encode_bert(text, vocab_path [, max_length]) → Struct{input_ids: Int64[],
/// attention_mask: Int64[], token_type_ids: Int64[]}</c>. Tokenizes
/// the input text with a BERT/WordPiece tokenizer (vocab.txt one
/// wordpiece per line, lowercase-uncased defaults) and packages the three
/// tensors that BERT-family encoders expect into one struct. The output
/// field names match the canonical ONNX input names so multi-input
/// <c>infer({encoded}, {...})</c> in a SQL model body lines up by name.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Truncation.</strong> The optional <c>max_length</c> caps the
/// total emitted token count (including <c>[CLS]</c> and <c>[SEP]</c>) at
/// that many tokens — long inputs are clipped from the tail, then the
/// special tokens are reapplied. Omit or pass NULL for no truncation.
/// Every BERT-family encoder has a fixed position-embedding table (commonly
/// 512); inputs beyond that index out of range and abort inside the ONNX
/// embeddings layer, so a SQL body driving such a model should pass
/// <c>max_length =&gt; 512</c> (or whatever the model's documented cap is).
/// </para>
/// </remarks>
public sealed class TokenizerEncodeBertFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "encode_bert";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Encoding;

    /// <inheritdoc />
    public static string Description =>
        "Encodes text with a BERT/WordPiece tokenizer (vocab.txt path) and returns "
        + "a struct containing input_ids, attention_mask, and token_type_ids — the "
        + "canonical BERT input bundle. Special tokens [CLS]/[SEP] are added; mask "
        + "is all-1s (no padding); token_type_ids is all-0s (single sequence). "
        + "Optional max_length truncates the total sequence (including specials); "
        + "omit or pass NULL for no truncation. "
        + "Designed to feed multi-input infer() in a CREATE MODEL body directly.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("text",       DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("vocab_path", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("max_length", DataKindMatcher.Family(DataKindFamily.IntegerFamily),
                    IsOptional: true),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Struct)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<TokenizerEncodeBertFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullStruct(0));
        }

        string text       = args[0].AsString();
        string vocabPath  = TokenizerPath.ResolveAbsoluteOrCatalogRelative(
            args[1].AsString(), "tokenizer.encode_bert", frame);

        int? maxLength = ReadOptionalMaxLength(args, index: 2, "tokenizer.encode_bert");

        BertTokenizer tokenizer = TokenizerCache.GetBertFromVocab(vocabPath);
        return new ValueTask<ValueRef>(
            TokenizerOps.EncodeBertToValueRef(tokenizer, text, maxLength, frame.Types));
    }

    /// <summary>
    /// Reads an optional max-length argument at <paramref name="index"/>:
    /// returns <c>null</c> if the slot is omitted or carries a SQL NULL
    /// (callers want no truncation), throws on a non-positive value.
    /// </summary>
    internal static int? ReadOptionalMaxLength(
        ReadOnlySpan<ValueRef> args, int index, string callerContext)
    {
        if (args.Length <= index || args[index].IsNull) return null;
        int value = args[index].ToInt32();
        if (value <= 2)
        {
            throw new FunctionArgumentException(callerContext,
                $"max_length must be > 2 to leave room for [CLS] and [SEP]; got {value}.");
        }
        return value;
    }
}

/// <summary>
/// <c>tokenizer.encode_bert_pair(query, passage, vocab_path) → Struct{input_ids: Int64[],
/// attention_mask: Int64[], token_type_ids: Int64[]}</c>. Encodes a pair of texts
/// into the standard BERT cross-encoder packing
/// (<c>[CLS] query [SEP] passage [SEP]</c>) with the per-segment
/// <c>token_type_ids</c> mask (<c>0</c> for the first segment incl. its
/// surrounding [CLS]/[SEP]; <c>1</c> for the second segment incl. its
/// trailing [SEP]) that BERT-family cross-encoders are trained with.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a separate function from <c>encode_bert</c>.</strong>
/// Cross-encoders (rerankers, NLI, paraphrase scoring) score the
/// relationship between two texts, and the model is trained with explicit
/// segment ids on the second sequence. <c>encode_bert</c> wraps a single
/// sequence with all-zero token_type_ids — fine for single-text BERT
/// encoders but degrades reranker quality because the model loses its
/// query-vs-passage signal.
/// </para>
/// <para>
/// <strong>No native pair API.</strong> Microsoft.ML.Tokenizers' BertTokenizer
/// has no pair-encode method; this function does the assembly manually —
/// tokenize each side without special tokens, then prepend
/// <see cref="BertTokenizer.ClassificationTokenId"/>, separate / terminate
/// with <see cref="BertTokenizer.SeparatorTokenId"/>, and build the
/// token_type_ids vector to match. Mirrors HuggingFace transformers'
/// <c>tokenizer(query, passage)</c> output byte-for-byte for the
/// common-case uncased English BERT vocab.
/// </para>
/// </remarks>
public sealed class TokenizerEncodeBertPairFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "encode_bert_pair";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Encoding;

    /// <inheritdoc />
    public static string Description =>
        "Encodes a (query, passage) pair with a BERT/WordPiece tokenizer "
        + "(vocab.txt path) and returns the canonical cross-encoder bundle: "
        + "input_ids ([CLS] q [SEP] p [SEP]), attention_mask (all 1s), and "
        + "token_type_ids (0 for q+surrounding specials, 1 for p+trailing SEP). "
        + "Optional max_length caps the total sequence length (longest-first "
        + "truncation between the two sides, matching HuggingFace tokenizers' "
        + "default pair-truncation strategy); omit or pass NULL for no truncation. "
        + "Feeds multi-input infer() in a CREATE MODEL body for rerankers, NLI, "
        + "and paraphrase models.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("query",      DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("passage",    DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("vocab_path", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("max_length", DataKindMatcher.Family(DataKindFamily.IntegerFamily),
                    IsOptional: true),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Struct)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<TokenizerEncodeBertPairFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullStruct(0));
        }

        string query      = args[0].AsString();
        string passage    = args[1].AsString();
        string vocabPath  = TokenizerPath.ResolveAbsoluteOrCatalogRelative(
            args[2].AsString(), "tokenizer.encode_bert_pair", frame);

        // Pair packing needs room for [CLS] + two [SEP]s, so a max_length of
        // 3 leaves zero content tokens; reject anything below 4.
        int? maxLength = args.Length > 3 && !args[3].IsNull
            ? args[3].ToInt32()
            : (int?)null;
        if (maxLength is int ml && ml < 4)
        {
            throw new FunctionArgumentException("tokenizer.encode_bert_pair",
                $"max_length must be >= 4 to leave room for [CLS], [SEP], [SEP]; got {ml}.");
        }

        BertTokenizer tokenizer = TokenizerCache.GetBertFromVocab(vocabPath);
        return new ValueTask<ValueRef>(
            TokenizerOps.EncodeBertPairToValueRef(tokenizer, query, passage, maxLength, frame.Types));
    }
}

/// <summary>
/// <c>tokenizer.encode_roberta(text, tokenizer_json_path) → Struct{input_ids: Int64[],
/// attention_mask: Int64[]}</c>. Tokenizes the input text with a RoBERTa-family
/// BPE tokenizer (loaded from a HuggingFace <c>tokenizer.json</c>) and packages
/// the two-tensor bundle that RoBERTa-family ONNX encoders expect.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a separate function from <c>encode_bert</c>.</strong> RoBERTa
/// uses byte-level BPE (not WordPiece), has different special tokens
/// (<c>&lt;s&gt;=0</c> / <c>&lt;/s&gt;=2</c> instead of [CLS]/[SEP]), and the
/// ONNX export takes only two inputs — <c>input_ids</c> + <c>attention_mask</c>
/// — without the third <c>token_type_ids</c> tensor BERT requires. Bundling
/// those differences behind <c>encode_roberta</c> keeps the SQL bodies for
/// RoBERTa-based classifiers (sentiment, NER, etc.) one-liner-clean.
/// </para>
/// <para>
/// <strong>Special tokens.</strong> Hard-codes the canonical RoBERTa special
/// token ids (<c>&lt;s&gt;=0</c> prepended, <c>&lt;/s&gt;=2</c> appended).
/// Every standard RoBERTa fine-tune uses these. Models that redefine the
/// special-token ids in their <c>tokenizer.json</c> would need a per-call
/// parameter; defer until a real consumer needs that flexibility.
/// </para>
/// </remarks>
public sealed class TokenizerEncodeRobertaFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "encode_roberta";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Encoding;

    /// <inheritdoc />
    public static string Description =>
        "Encodes text with a RoBERTa-family BPE tokenizer (tokenizer.json path) and returns "
        + "a struct containing input_ids and attention_mask — the two-tensor bundle RoBERTa "
        + "ONNX encoders expect (no token_type_ids). Special tokens <s> (id 0) and </s> "
        + "(id 2) are prepended/appended. Designed to feed multi-input infer() in a "
        + "CREATE MODEL body directly.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("text",                DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("tokenizer_json_path", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Struct)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<TokenizerEncodeRobertaFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullStruct(0));
        }

        string text             = args[0].AsString();
        string tokenizerJsonPath = TokenizerPath.ResolveAbsoluteOrCatalogRelative(
            args[1].AsString(), "tokenizer.encode_roberta", frame);

        BpeTokenizer tokenizer = TokenizerCache.GetFromTokenizerJson(tokenizerJsonPath);
        return new ValueTask<ValueRef>(TokenizerOps.EncodeRobertaToValueRef(tokenizer, text, frame.Types));
    }
}

/// <summary>
/// Path resolution for the scalar tokenizer functions. Scalar evaluation
/// frames don't carry a <c>ModelCatalog</c> reference today, so we can only
/// honour the two non-catalog forms: <c>file://</c> URI and OS-absolute path.
/// Relative paths get a clear error pointing at the limitation; the
/// follow-up to thread catalog access through <c>EvaluationFrame</c> would
/// retire this restriction.
/// </summary>
internal static class TokenizerPath
{
    internal static string ResolveAbsolute(string path, string callerContext)
    {
        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return path["file://".Length..];
        }
        if (!Path.IsPathRooted(path))
        {
            throw new FunctionArgumentException(callerContext,
                $"'{path}' is a relative path. Scalar tokenizer functions called outside a " +
                "CREATE MODEL body don't have access to the catalog's model directory; pass " +
                "an absolute path or a 'file://'-prefixed URI. Inside a CREATE MODEL body, " +
                "relative paths resolve against the directory of the model's USING file.");
        }
        return path;
    }

    /// <summary>
    /// Same contract as <see cref="ResolveAbsolute(string, string)"/> but
    /// also accepts a path relative to the calling model's resolved
    /// <c>USING</c> directory (<c>frame.CurrentModel.ResolvedUsingPath</c>). The
    /// canonical layout for a sentence-transformer entry is
    /// <c>{ModelDirectory}/{catalog-id}/model.onnx</c> +
    /// <c>vocab.txt</c> sibling — relative resolution here turns the SQL
    /// body's <c>'vocab.txt'</c> into the absolute path without forcing
    /// the body to hardcode <c>$DATUMV_MODELS</c> or <c>file://</c>.
    /// </summary>
    internal static string ResolveAbsoluteOrCatalogRelative(
        string path, string callerContext, EvaluationFrame frame)
    {
        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return path["file://".Length..];
        }
        if (Path.IsPathRooted(path)) return path;

        if (frame.CurrentModel is { ResolvedUsingPath: { } resolved })
        {
            string? modelDir = Path.GetDirectoryName(resolved);
            if (!string.IsNullOrEmpty(modelDir))
            {
                return Path.GetFullPath(Path.Combine(modelDir, path));
            }
        }
        throw new FunctionArgumentException(callerContext,
            $"'{path}' is a relative path. Catalog-relative resolution requires a CREATE MODEL " +
            "body frame; outside one, pass an absolute path or a 'file://'-prefixed URI.");
    }
}

/// <summary>
/// Shared encode/decode logic between the four tokenizer functions. Extracts
/// the per-call concerns (Int32↔Int64 conversion, reading the ids array
/// regardless of its <c>ValueRef</c> payload shape) into one place.
/// </summary>
internal static class TokenizerOps
{
    /// <summary>
    /// Runs the tokenizer's encode pass on <paramref name="text"/> and packs
    /// the resulting Int32 ids into an Int64 array (the SQL surface is
    /// Int64-canonical for token ids; Microsoft.ML.Tokenizers returns Int32).
    /// </summary>
    internal static ValueRef EncodeToValueRef(BpeTokenizer tokenizer, string text)
    {
        // EncodeToIds(text) uses the tokenizer's default normalizer / pre-
        // tokenizer; v1 BPE tokenizers loaded from tokenizer.json/vocab+merges
        // carry neither configuration so no BOS/EOS gets added automatically —
        // raw segmentation, matching the documented "no special tokens by
        // default" contract.
        IReadOnlyList<int> ids = tokenizer.EncodeToIds(text);
        long[] out64 = new long[ids.Count];
        for (int i = 0; i < ids.Count; i++) out64[i] = ids[i];
        return ValueRef.FromPrimitiveArray(out64, DataKind.Int64);
    }

    /// <summary>
    /// Reads the id array off <paramref name="idsArg"/> (accepting both the
    /// <c>FromPrimitiveArray</c> typed payload and the inline <c>ValueRef[]</c>
    /// form), narrows each Int64 to Int32 for the tokenizer call, and returns
    /// the decoded string as a <see cref="ValueRef"/>.
    /// </summary>
    internal static ValueRef DecodeToValueRef(BpeTokenizer tokenizer, ValueRef idsArg)
    {
        int[] ids32;
        if (idsArg.Materialized is long[] direct)
        {
            ids32 = new int[direct.Length];
            for (int i = 0; i < direct.Length; i++) ids32[i] = checked((int)direct[i]);
        }
        else
        {
            ReadOnlySpan<ValueRef> elements = idsArg.GetArrayElements();
            ids32 = new int[elements.Length];
            for (int i = 0; i < elements.Length; i++) ids32[i] = checked((int)elements[i].ToInt64());
        }

        string decoded = tokenizer.Decode(ids32) ?? string.Empty;
        return ValueRef.FromString(decoded);
    }

    /// <summary>
    /// Runs the BERT tokenizer with special tokens enabled, then builds a
    /// 3-field struct ValueRef carrying input_ids, attention_mask, and
    /// token_type_ids — the bundle every BERT-family ONNX encoder expects.
    /// The struct's TypeId is interned into <paramref name="types"/> when
    /// supplied so downstream <c>infer({encoded}, {...})</c> can resolve
    /// field names back to session input names.
    /// <para>
    /// When <paramref name="maxLength"/> is non-null, caps the total emitted
    /// token count (including <c>[CLS]</c> and <c>[SEP]</c>) at that many
    /// tokens — the tail of the sequence is dropped and the trailing
    /// <c>[SEP]</c> is reapplied, matching HuggingFace's default
    /// single-sequence truncation. <c>null</c> preserves the legacy no-cap
    /// behaviour for callers that don't pass one.
    /// </para>
    /// </summary>
    internal static ValueRef EncodeBertToValueRef(
        BertTokenizer tokenizer, string text, int? maxLength, TypeRegistry? types)
    {
        // BertTokenizer.EncodeToIds(addSpecialTokens=true) prepends [CLS]
        // and appends [SEP]. For a single sequence with no padding, the
        // attention mask is all-1s of the same length and token_type_ids
        // is all-0s. Microsoft.ML.Tokenizers returns Int32 ids; the SQL
        // surface (and ONNX BERT inputs) is Int64.
        IReadOnlyList<int> ids = tokenizer.EncodeToIds(
            text, addSpecialTokens: true, considerPreTokenization: true);
        int n = ids.Count;

        // Truncate to fit max_length while preserving the trailing [SEP].
        // The full sequence ends with [SEP] (id = tokenizer.SeparatorTokenId);
        // we drop content tokens between the last kept body token and the
        // [SEP], then keep [SEP] as the final token.
        if (maxLength is int cap && n > cap)
        {
            int sepId = tokenizer.SeparatorTokenId;
            long[] truncated = new long[cap];
            for (int i = 0; i < cap - 1; i++) truncated[i] = ids[i];
            truncated[cap - 1] = sepId;
            long[] mask = new long[cap];
            long[] tti = new long[cap];
            for (int i = 0; i < cap; i++) mask[i] = 1L;
            return PackBertStruct(truncated, mask, tti, types);
        }

        long[] inputIds = new long[n];
        long[] attentionMask = new long[n];
        long[] tokenTypeIds = new long[n];
        for (int i = 0; i < n; i++)
        {
            inputIds[i] = ids[i];
            attentionMask[i] = 1L;
            // tokenTypeIds left at default 0L — single-sequence default.
        }

        return PackBertStruct(inputIds, attentionMask, tokenTypeIds, types);
    }

    /// <summary>
    /// Packages the three BERT input arrays into a struct ValueRef whose
    /// field names match the canonical ONNX input names
    /// (<c>input_ids</c> / <c>attention_mask</c> / <c>token_type_ids</c>),
    /// interning the struct's type id into <paramref name="types"/> when
    /// supplied. Centralised so the no-truncation and truncation paths in
    /// the two BERT encoders share one struct-shape definition.
    /// </summary>
    private static ValueRef PackBertStruct(
        long[] inputIds, long[] attentionMask, long[] tokenTypeIds, TypeRegistry? types)
    {
        ValueRef[] fields =
        [
            ValueRef.FromPrimitiveArray(inputIds,      DataKind.Int64),
            ValueRef.FromPrimitiveArray(attentionMask, DataKind.Int64),
            ValueRef.FromPrimitiveArray(tokenTypeIds,  DataKind.Int64),
        ];

        ushort typeId = 0;
        if (types is not null)
        {
            int int64ArrayTypeId = types.InternArrayType(DataKind.Int64);
            StructFieldDescriptor[] descriptors =
            [
                new("input_ids",      int64ArrayTypeId),
                new("attention_mask", int64ArrayTypeId),
                new("token_type_ids", int64ArrayTypeId),
            ];
            typeId = (ushort)types.InternStructType(descriptors);
        }

        return ValueRef.FromStruct(fields, typeId);
    }

    /// <summary>
    /// Pair-encodes <paramref name="query"/> and <paramref name="passage"/>
    /// into the standard BERT cross-encoder shape
    /// (<c>[CLS] query [SEP] passage [SEP]</c>) with matching attention_mask
    /// and per-segment token_type_ids. The struct's TypeId is interned into
    /// <paramref name="types"/> when supplied so downstream
    /// <c>infer({encoded}, {...})</c> can resolve field names back to
    /// session input names.
    /// </summary>
    /// <remarks>
    /// Microsoft.ML.Tokenizers' BertTokenizer has no pair-encode method;
    /// this method does the assembly manually using
    /// <see cref="BertTokenizer.ClassificationTokenId"/> and
    /// <see cref="BertTokenizer.SeparatorTokenId"/>. Each side is tokenized
    /// with <c>addSpecialTokens: false</c> to avoid double-wrapping, then
    /// glued together with the right specials in the right positions.
    /// </remarks>
    internal static ValueRef EncodeBertPairToValueRef(
        BertTokenizer tokenizer, string query, string passage, int? maxLength, TypeRegistry? types)
    {
        IReadOnlyList<int> queryIds = tokenizer.EncodeToIds(
            query, addSpecialTokens: false, considerPreTokenization: true);
        IReadOnlyList<int> passageIds = tokenizer.EncodeToIds(
            passage, addSpecialTokens: false, considerPreTokenization: true);

        int clsId = tokenizer.ClassificationTokenId;
        int sepId = tokenizer.SeparatorTokenId;

        // Layout: [CLS] q1..qN [SEP] p1..pM [SEP]
        //         seg=0 seg=0   seg=0 seg=1  seg=1
        // attention_mask all 1s (no padding at this layer).
        int qLen = queryIds.Count;
        int pLen = passageIds.Count;

        // Longest-first truncation. When the assembled length would exceed
        // max_length, repeatedly trim one token from whichever side is
        // currently longer until both sides plus the 3 specials fit. Matches
        // HuggingFace's default truncation strategy for sentence-pair tasks
        // and keeps the overall query/passage balance closer than truncating
        // a single side outright. The 3 fixed tokens are the leading [CLS]
        // and the two [SEP]s.
        if (maxLength is int cap)
        {
            int budget = cap - 3;
            while (qLen + pLen > budget)
            {
                if (qLen >= pLen) qLen--;
                else pLen--;
            }
        }

        int total = 1 + qLen + 1 + pLen + 1;

        long[] inputIds      = new long[total];
        long[] attentionMask = new long[total];
        long[] tokenTypeIds  = new long[total];

        int pos = 0;
        inputIds[pos]      = clsId;
        attentionMask[pos] = 1L;
        tokenTypeIds[pos]  = 0L;
        pos++;

        for (int i = 0; i < qLen; i++, pos++)
        {
            inputIds[pos]      = queryIds[i];
            attentionMask[pos] = 1L;
            tokenTypeIds[pos]  = 0L;
        }

        inputIds[pos]      = sepId;
        attentionMask[pos] = 1L;
        tokenTypeIds[pos]  = 0L;
        pos++;

        for (int i = 0; i < pLen; i++, pos++)
        {
            inputIds[pos]      = passageIds[i];
            attentionMask[pos] = 1L;
            tokenTypeIds[pos]  = 1L;
        }

        inputIds[pos]      = sepId;
        attentionMask[pos] = 1L;
        tokenTypeIds[pos]  = 1L;

        return PackBertStruct(inputIds, attentionMask, tokenTypeIds, types);
    }

    /// <summary>
    /// Runs the BPE tokenizer on <paramref name="text"/>, prepends/appends the
    /// canonical RoBERTa special-token ids (<c>&lt;s&gt;=0</c> / <c>&lt;/s&gt;=2</c>),
    /// and packages a 2-field struct ValueRef carrying input_ids and
    /// attention_mask — the bundle every RoBERTa-family ONNX encoder expects
    /// (no token_type_ids). The struct's TypeId is interned into
    /// <paramref name="types"/> when supplied so downstream multi-input
    /// <c>infer({encoded}, {...})</c> resolves field names back to session
    /// input names.
    /// </summary>
    internal static ValueRef EncodeRobertaToValueRef(
        BpeTokenizer tokenizer, string text, TypeRegistry? types)
    {
        // BpeTokenizer.EncodeToIds doesn't add special tokens automatically.
        // Wrap the raw segmentation with the canonical RoBERTa BOS/EOS pair:
        // every standard RoBERTa fine-tune (and Xenova's exports) uses
        // <s>=0 at the start, </s>=2 at the end. Attention mask is all-1s
        // for the wrapped sequence; no padding for batch=1.
        IReadOnlyList<int> ids = tokenizer.EncodeToIds(text);
        int n = ids.Count + 2; // +2 for <s> and </s>

        long[] inputIds = new long[n];
        long[] attentionMask = new long[n];
        inputIds[0] = 0L; // <s>
        for (int i = 0; i < ids.Count; i++)
        {
            inputIds[i + 1] = ids[i];
        }
        inputIds[n - 1] = 2L; // </s>
        for (int i = 0; i < n; i++)
        {
            attentionMask[i] = 1L;
        }

        ValueRef[] fields =
        [
            ValueRef.FromPrimitiveArray(inputIds,      DataKind.Int64),
            ValueRef.FromPrimitiveArray(attentionMask, DataKind.Int64),
        ];

        ushort typeId = 0;
        if (types is not null)
        {
            int int64ArrayTypeId = types.InternArrayType(DataKind.Int64);
            StructFieldDescriptor[] descriptors =
            [
                new("input_ids",      int64ArrayTypeId),
                new("attention_mask", int64ArrayTypeId),
            ];
            typeId = (ushort)types.InternStructType(descriptors);
        }

        return ValueRef.FromStruct(fields, typeId);
    }
}
