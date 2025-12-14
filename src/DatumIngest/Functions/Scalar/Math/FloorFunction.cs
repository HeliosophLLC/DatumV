using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Math;

/// <summary>
/// Returns the largest integer not greater than the input value.
/// Integer kinds are returned unchanged. The result kind matches the input kind.
/// Null input propagates to null output.
/// </summary>
public sealed class FloorFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "floor";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Returns the largest integer not greater than the input. Result kind matches the input kind.";

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
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<FloorFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueRef Execute(ReadOnlySpan<ValueRef> arguments, in EvaluationFrame frame)
    {
        ValueRef input = arguments[0];
        if (input.IsNull)
            return ValueRef.Null(input.Kind);

        return input.Kind switch
        {
            DataKind.Int8 or DataKind.Int16 or DataKind.Int32 or DataKind.Int64 or DataKind.Int128 or
            DataKind.UInt8 or DataKind.UInt16 or DataKind.UInt32 or DataKind.UInt64 or DataKind.UInt128
                => input,

            DataKind.Float16 => ValueRef.FromFloat16(
                (Half)MathF.Floor((float)input.AsFloat16())),
            DataKind.Float32 => ValueRef.FromFloat32(
                MathF.Floor(input.AsFloat32())),
            DataKind.Float64 => ValueRef.FromFloat64(
                System.Math.Floor(input.AsFloat64())),
            DataKind.Decimal => ValueRef.FromDecimal(
                System.Math.Floor(input.AsDecimal())),

            _ => throw new FunctionArgumentException(Name, $"does not support kind {input.Kind}."),
        };
    }
}
