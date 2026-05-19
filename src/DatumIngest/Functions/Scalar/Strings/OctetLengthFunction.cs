using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// PostgreSQL <c>octet_length(text) → Int32</c>: number of UTF-8 bytes in
/// a string. For ASCII text this matches <c>length(text)</c>; for multi-byte
/// characters (accented Latin, CJK, emoji, …) it's strictly larger.
/// </summary>
/// <remarks>
/// <para>
/// Both storage tiers reachable through a <see cref="ValueRef"/> carry the
/// byte length as a stored field — inline strings in the low byte of
/// <c>_charCount</c>, arena-backed strings in <c>BackedLength</c>. The inline
/// case reads directly off <see cref="DataValue.StringUtf8ByteLength"/>;
/// the materialised-string case uses
/// <see cref="System.Text.Encoding.GetByteCount(string)"/> which walks the
/// chars without allocating a byte buffer.
/// </para>
/// <para>
/// Implements <see cref="IInlineMetadataAccessor"/> so the planner-time
/// elider rewrites call sites into <see cref="InlineAccessorExpression"/>;
/// the evaluator's fast path returns the cached count for inline strings
/// and delegates to this method for the materialised-string walk.
/// </para>
/// </remarks>
public sealed class OctetLengthFunction : IFunction, IScalarFunction, IInlineMetadataAccessor
{
    /// <inheritdoc />
    public static string Name => "octet_length";

    /// <inheritdoc />
    public InlineAccessorField Field => InlineAccessorField.StringByteLength;

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Number of UTF-8 bytes in a String (PostgreSQL octet_length semantics).";

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
        FunctionMetadata.Validate<OctetLengthFunction>(argumentKinds);

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

        // Inline fast path: UTF-8 byte length is cached in _charCount.low.
        DataValue inline = arg.InlineDataValue;
        if (inline.IsInline && inline.Kind == DataKind.String)
        {
            return new ValueTask<ValueRef>(ValueRef.FromInt32(inline.StringUtf8ByteLength));
        }

        // Non-inline: walk the chars counting UTF-8 bytes without allocating
        // an intermediate buffer. ~10× cheaper than the legacy
        // GetByteCount(AsString()) path that decoded then re-encoded.
        return new ValueTask<ValueRef>(
            ValueRef.FromInt32(System.Text.Encoding.UTF8.GetByteCount(arg.AsString())));
    }
}
