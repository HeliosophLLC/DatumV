using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Arrays;

/// <summary>
/// PostgreSQL-compatible <c>array_length(arr, dim)</c>: returns the size of
/// the requested dimension (1-based) of an array. For a 1-D array,
/// <c>array_length(arr, 1)</c> equals the element count. For a multi-dim
/// array (<c>Array&lt;T&gt;(n, m, …)</c>), <c>array_length(arr, k)</c>
/// returns the <c>k</c>-th dimension's size as declared in the column's
/// fixed shape or carried by the value's runtime shape prefix.
/// Returns null for nulls and for out-of-range dimensions. Use
/// <c>cardinality(arr)</c> for the total (flat) element count.
/// </summary>
public sealed class ArrayLengthFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "array_length";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Array;

    /// <inheritdoc />
    public static string Description =>
        "Returns the size of the requested array dimension (1-based). "
        + "PostgreSQL-compatible. Use cardinality(arr) for the total flat "
        + "element count.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("array", DataKindMatcher.Any, IsArray: ArrayMatch.Array),
                new ParameterSpec("dim", DataKindMatcher.Family(DataKindFamily.NumericScalar), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ArrayLengthFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef arrayArg = args[0];
        if (arrayArg.IsNull || args[1].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Int32));
        }

        int dim = args[1].ToInt32();
        DataValue source = arrayArg.ToDataValue(frame.Source);

        int ndim = source.IsMultiDim ? source.Ndim : 1;
        if (dim < 1 || dim > ndim)
        {
            // PG returns NULL for an out-of-range dim rather than raising.
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Int32));
        }

        if (source.IsMultiDim)
        {
            ReadOnlySpan<int> shape = source.GetShape(frame.Source, frame.SidecarRegistry);
            return new ValueTask<ValueRef>(ValueRef.FromInt32(shape[dim - 1]));
        }

        // Flat 1-D array: only dim=1 is in range (already enforced above).
        return new ValueTask<ValueRef>(ValueRef.FromInt32(arrayArg.GetArrayLength()));
    }
}
