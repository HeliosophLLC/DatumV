using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Arrays;

/// <summary>
/// <c>array_flatten(arr Array&lt;T&gt;) → Array&lt;T&gt;</c>. Returns a flat,
/// 1-D row-major view of a (possibly multi-dim) array — the shape attachment
/// is dropped, the element buffer is reused. The explicit inverse of a shaped
/// cast (<c>CAST(x AS Array&lt;T&gt;(h, w))</c> / <c>DECLARE x Array&lt;T&gt;(h, w)</c>).
/// </summary>
/// <remarks>
/// <para>
/// In this engine a value carries its shape through every binding and cast —
/// a bare <c>Array&lt;T&gt;</c> annotation preserves it. <c>array_flatten</c> is
/// the one place that deliberately discards the shape, for the cases that want
/// the raw row-major vector: an ONNX classifier head that emits <c>[1, N]</c>
/// before a <c>softmax</c> / <c>argmax</c> / single-index pick, or a decoder
/// output you slice element-wise. Without it, single-index <c>arr[i]</c> and
/// <c>array_slice</c> reject a multi-dim source.
/// </para>
/// <para>
/// Element-kind agnostic and zero-copy: a 1-D source returns unchanged; a
/// multi-dim source reuses its flat element buffer with the shape stripped.
/// </para>
/// </remarks>
public sealed class ArrayFlattenFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "array_flatten";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Array;

    /// <inheritdoc />
    public static string Description =>
        "Returns a flat 1-D row-major view of a (possibly multi-dim) array, "
        + "dropping the shape and reusing the element buffer. The explicit "
        + "counterpart to a shaped cast / DECLARE — use it when an ONNX output "
        + "arrives multi-dim (e.g. a [1, N] classifier head) and you want the "
        + "raw vector for softmax / argmax / single-index access. 1-D input "
        + "passes through unchanged.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("array", DataKindMatcher.Any, IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            // Same element kind as the source; only the shape changes.
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ArrayFlattenFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arrayArg = arguments.Span[0];
        if (arrayArg.IsNull)
        {
            // Typed-null array columns surface with IsArray=false but the
            // element Kind preserved on the carrier — fall back to Kind rather
            // than ArrayElementKind, which would throw.
            DataKind elementKind = arrayArg.IsArray ? arrayArg.ArrayElementKind : arrayArg.Kind;
            return new ValueTask<ValueRef>(ValueRef.NullArray(elementKind));
        }

        return new ValueTask<ValueRef>(arrayArg.AsFlatArray());
    }
}
