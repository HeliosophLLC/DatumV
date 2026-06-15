using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Arrays;

/// <summary>
/// Arithmetic mean of every element of a numeric typed array, returned as
/// <see cref="DataKind.Float32"/>. Multi-dim arrays reduce across the whole
/// flat element span. A null or empty array returns null. Non-numeric
/// element kinds are rejected — see <see cref="ArrayNumericReductionCore"/>
/// for the supported kinds.
/// </summary>
public sealed class ArrayAvgFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "array_avg";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Array;

    /// <inheritdoc />
    public static string Description =>
        "Arithmetic mean of every element of a numeric typed array, returned "
        + "as Float32. Multi-dim arrays reduce across the whole tensor. "
        + "Empty and null arrays return null.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("array",
                    DataKindMatcher.Family(DataKindFamily.NumericScalar),
                    IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ArrayAvgFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arrayArg = arguments.Span[0];
        if (arrayArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32));
        }

        (double sum, int count) = ArrayNumericReductionCore.SumAndCount(arrayArg, frame, Name);
        if (count == 0)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32));
        }
        return new ValueTask<ValueRef>(ValueRef.FromFloat32((float)(sum / count)));
    }
}
