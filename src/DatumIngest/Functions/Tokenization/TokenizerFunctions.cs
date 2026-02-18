using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

using Microsoft.ML.Tokenizers;

namespace DatumIngest.Functions.Tokenization;

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
        string resolvedPath = TokenizerPath.ResolveAbsolute(args[1].AsString(), "tokenizer.encode");

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
        string resolvedVocab  = TokenizerPath.ResolveAbsolute(args[1].AsString(), "tokenizer.encode_bpe");
        string resolvedMerges = TokenizerPath.ResolveAbsolute(args[2].AsString(), "tokenizer.encode_bpe");

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

        string resolvedPath = TokenizerPath.ResolveAbsolute(args[1].AsString(), "tokenizer.decode");

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

        string resolvedVocab  = TokenizerPath.ResolveAbsolute(args[1].AsString(), "tokenizer.decode_bpe");
        string resolvedMerges = TokenizerPath.ResolveAbsolute(args[2].AsString(), "tokenizer.decode_bpe");

        BpeTokenizer tokenizer = TokenizerCache.GetFromVocabMerges(resolvedVocab, resolvedMerges);
        return new ValueTask<ValueRef>(TokenizerOps.DecodeToValueRef(tokenizer, args[0]));
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
                $"'{path}' is a relative path. Scalar tokenizer functions don't have access " +
                "to the catalog's model directory; pass an absolute path or a 'file://'-prefixed URI. " +
                "(Catalog-relative resolution is a planned EvaluationFrame extension.)");
        }
        return path;
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
}
