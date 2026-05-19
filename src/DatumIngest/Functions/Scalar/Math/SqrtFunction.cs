using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Math;

/// <summary>
/// Returns the square root of a numeric input as <see cref="DataKind.Float64"/>.
/// Negative inputs surface as <see cref="double.NaN"/>, matching
/// <see cref="System.Math.Sqrt"/>. Null input propagates to null output.
/// </summary>
public sealed class SqrtFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "sqrt";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Returns the square root of a numeric input as Float64.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float64)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<SqrtFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef input = arguments.Span[0];
        if (input.IsNull)
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float64));

        input.TryToDouble(out double v);
        return new ValueTask<ValueRef>(ValueRef.FromFloat64(System.Math.Sqrt(v)));
    }
}
