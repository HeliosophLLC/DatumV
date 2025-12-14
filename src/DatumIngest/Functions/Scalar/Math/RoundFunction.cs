using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Math;

/// <summary>
/// Rounds a numeric value to the specified number of decimal places (default 0).
/// Integer kinds are returned unchanged. Uses midpoint-rounding away from zero,
/// matching PostgreSQL semantics. Null input propagates to null output.
/// </summary>
public sealed class RoundFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "round";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Rounds a numeric value to the specified number of decimal places (default 0). " +
        "Uses midpoint-rounding away from zero.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(0)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("decimals", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<RoundFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueRef Execute(ReadOnlySpan<ValueRef> arguments, in EvaluationFrame frame)
    {
        ValueRef input = arguments[0];
        if (input.IsNull)
            return ValueRef.Null(input.Kind);

        int decimals = 0;
        if (arguments.Length >= 2 && !arguments[1].IsNull)
        {
            arguments[1].TryToDouble(out double d);
            decimals = (int)d;
        }

        return input.Kind switch
        {
            DataKind.Int8 or DataKind.Int16 or DataKind.Int32 or DataKind.Int64 or DataKind.Int128 or
            DataKind.UInt8 or DataKind.UInt16 or DataKind.UInt32 or DataKind.UInt64 or DataKind.UInt128
                => input,

            DataKind.Float16 => ValueRef.FromFloat16(
                (Half)MathF.Round((float)input.AsFloat16(), decimals, MidpointRounding.AwayFromZero)),
            DataKind.Float32 => ValueRef.FromFloat32(
                MathF.Round(input.AsFloat32(), decimals, MidpointRounding.AwayFromZero)),
            DataKind.Float64 => ValueRef.FromFloat64(
                System.Math.Round(input.AsFloat64(), decimals, MidpointRounding.AwayFromZero)),
            DataKind.Decimal => ValueRef.FromDecimal(
                System.Math.Round(input.AsDecimal(), decimals, MidpointRounding.AwayFromZero)),

            _ => throw new FunctionArgumentException(Name, $"does not support kind {input.Kind}."),
        };
    }
}
