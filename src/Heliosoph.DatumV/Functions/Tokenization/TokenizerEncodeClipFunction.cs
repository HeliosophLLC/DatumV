using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

using Microsoft.ML.Tokenizers;

namespace Heliosoph.DatumV.Functions.Tokenization;

/// <summary>
/// <c>tokenizer.encode_clip(text, vocab_path, merges_path) → Int64[77]</c>.
/// Builds the CLIP text-encoder input frame: lowercase the prompt,
/// BPE-segment it, then wrap as <c>[BOS=49406, ...ids, EOS=49407, EOS, ..., EOS]</c>
/// truncated/padded to exactly 77 tokens. The canonical input shape for
/// OpenAI's CLIP (used by SD 1.x / 2.x / SDXL text encoders).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a separate function from <c>encode_bpe</c>.</strong> CLIP's
/// framing convention (lowercase, fixed-length, EOS padding, specific
/// BOS / EOS ids) is shared across every SD-family model and bakes
/// 5+ lines of error-prone array surgery into one call. The id constants
/// 49406 / 49407 are CLIP-vocabulary-specific — they don't generalize to
/// other BPE tokenizers, so a dedicated function is cleaner than parameterizing
/// <c>encode_bpe</c>.
/// </para>
/// <para>
/// <strong>Catalog-relative paths.</strong> Mirrors <c>encode_bpe</c>:
/// inside a CREATE MODEL body, relative paths resolve against the calling
/// model's USING directory (so an SD body's <c>'../tokenizer/vocab.json'</c>
/// reaches the sibling tokenizer folder when the USING file is in
/// <c>sd-turbo-onnx/text_encoder/</c>).
/// </para>
/// </remarks>
public sealed class TokenizerEncodeClipFunction : IFunction, IScalarFunction
{
    // CLIP tokenizer constants (shared across SD 1.x / 2.x / SDXL).
    private const int ClipMaxTokens = 77;
    private const int BosTokenId = 49406; // <|startoftext|>
    private const int EosTokenId = 49407; // <|endoftext|>

    /// <inheritdoc />
    public static string Name => "encode_clip";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Encoding;

    /// <inheritdoc />
    public static string Description =>
        "Encodes a prompt with the CLIP BPE tokenizer and wraps it as [BOS=49406, ...ids, EOS=49407, " +
        "EOS, ..., EOS] of length 77. Lowercases the prompt first (CLIP convention). Used by every " +
        "SD-family text encoder. Catalog-relative paths supported inside CREATE MODEL bodies.";

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
        FunctionMetadata.Validate<TokenizerEncodeClipFunction>(argumentKinds);

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
        string resolvedVocab  = TokenizerPath.ResolveAbsoluteOrCatalogRelative(
            args[1].AsString(), "tokenizer.encode_clip", frame);
        string resolvedMerges = TokenizerPath.ResolveAbsoluteOrCatalogRelative(
            args[2].AsString(), "tokenizer.encode_clip", frame);

        BpeTokenizer tokenizer = TokenizerCache.GetFromVocabMerges(resolvedVocab, resolvedMerges);
        IReadOnlyList<int> bpeTokens = tokenizer.EncodeToIds(text.ToLowerInvariant());

        long[] result = new long[ClipMaxTokens];
        result[0] = BosTokenId;
        int promptCount = Math.Min(bpeTokens.Count, ClipMaxTokens - 2);
        for (int i = 0; i < promptCount; i++)
        {
            result[i + 1] = bpeTokens[i];
        }
        // Append EOS and pad with EOS — CLIP's documented convention.
        for (int i = promptCount + 1; i < ClipMaxTokens; i++)
        {
            result[i] = EosTokenId;
        }

        return new ValueTask<ValueRef>(ValueRef.FromPrimitiveArray(result, DataKind.Int64));
    }
}
