using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Arrays;

/// <summary>
/// Returns the number of dimensions of an array. Mirrors PostgreSQL's
/// <c>array_ndims()</c>. Flat (1-D) arrays return <c>1</c>; multi-dim arrays
/// carrying an explicit shape return <see cref="DataValue.Ndim"/>. Null
/// array yields a null result.
/// </summary>
public sealed class ArrayNdimsFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "array_ndims";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Array;

    /// <inheritdoc />
    public static string Description =>
        "Returns the number of dimensions of an array. PostgreSQL-compatible.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("array", DataKindMatcher.Any, IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ArrayNdimsFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arrayArg = arguments.Span[0];
        if (arrayArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Int32));
        }
        int ndim = arrayArg.IsMultiDim ? arrayArg.Ndim : 1;
        return new ValueTask<ValueRef>(ValueRef.FromInt32(ndim));
    }
}
