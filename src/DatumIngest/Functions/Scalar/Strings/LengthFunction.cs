using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Strings;

/// <summary>
/// PostgreSQL <c>length(text) → Int32</c>: number of Unicode code points in
/// a string. Matches PG behaviour for surrogate-pair characters — emoji,
/// ancient scripts, and mathematical alphanumerics each count as 1, in
/// contrast to <see cref="string.Length"/> which counts UTF-16 code units.
/// </summary>
/// <remarks>
/// <para>
/// Inline strings (≤ 27 UTF-8 bytes) carry the code-point count cached in
/// the high byte of <c>_charCount</c> at construction
/// (<see cref="DataValue.InlineStringCodePointCount"/>) — direct field read.
/// Non-inline strings reach the function as a materialised
/// <see cref="string"/> on the <see cref="ValueRef"/>; the original cached
/// count on the arena/sidecar <see cref="DataValue"/> doesn't survive the
/// lift, so this path walks the string's <see cref="System.Text.Rune"/>
/// enumeration. Single pass, no allocations.
/// </para>
/// <para>
/// Implements <see cref="IInlineMetadataAccessor"/> so the planner-time
/// elider rewrites call sites into <see cref="InlineAccessorExpression"/>;
/// the evaluator's fast path returns the cached count directly for inline
/// strings and delegates back to this method's walk for non-inline strings
/// (same fallback shape as the Phase 1 image accessors).
/// </para>
/// </remarks>
public sealed class LengthFunction : IFunction, IScalarFunction, IInlineMetadataAccessor
{
    /// <inheritdoc />
    public static string Name => "length";

    /// <inheritdoc />
    public InlineAccessorField Field => InlineAccessorField.StringCodePointLength;

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Number of Unicode code points in a String (PostgreSQL length semantics — "
        + "surrogate-pair characters count as 1).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("value", DataKindMatcher.Exact(DataKind.String))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<LengthFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Int32));
        }

        // Inline fast path: code-point count is cached in _charCount.high.
        DataValue inline = arg.InlineDataValue;
        if (inline.IsInline && inline.Kind == DataKind.String)
        {
            return new ValueTask<ValueRef>(ValueRef.FromInt32(inline.InlineStringCodePointCount));
        }

        // Non-inline: walk Runes over the materialised string. Cheaper than
        // re-encoding to UTF-8 and counting non-continuation bytes (which would
        // both allocate a byte[] and run a second pass).
        int count = 0;
        foreach (System.Text.Rune _ in arg.AsString().EnumerateRunes())
        {
            count++;
        }
        return new ValueTask<ValueRef>(ValueRef.FromInt32(count));
    }
}
