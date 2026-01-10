using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Encodes a scalar value as a two-element Float32 array using sine and cosine
/// for cyclical features: <c>[sin(2π·value/period), cos(2π·value/period)]</c>.
/// Both arguments null yield a null result.
/// </summary>
/// <remarks>
/// Designed for temporal feature encoding:
/// <c>cyclical_encode(date_part('month', d), 12)</c> encodes the month as a point
/// on the unit circle, preserving the cyclical relationship between December and January.
/// </remarks>
public sealed class CyclicalEncodeFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "cyclical_encode";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Temporal;

    /// <inheritdoc />
    public static string Description =>
        "Returns [sin(2π·value/period), cos(2π·value/period)] as a Float32 array.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("period", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<CyclicalEncodeFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef valueArg = args[0];
        ValueRef periodArg = args[1];

        if (valueArg.IsNull || periodArg.IsNull)
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.Float32));

        valueArg.TryToDouble(out double v);
        periodArg.TryToDouble(out double period);

        double angle = 2.0 * System.Math.PI * v / period;
        return new ValueTask<ValueRef>(ValueRef.FromPrimitiveArray<float>(
            [(float)System.Math.Sin(angle), (float)System.Math.Cos(angle)],
            DataKind.Float32));
    }
}
