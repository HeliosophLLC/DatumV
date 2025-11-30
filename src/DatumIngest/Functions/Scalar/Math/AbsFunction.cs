using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Math;

/// <summary>
/// Returns the absolute value of a numeric input. The result kind is the
/// same as the input kind. Null input propagates to null output.
/// </summary>
/// <remarks>
/// <para>
/// Unsigned integer kinds are returned unchanged — their values are
/// non-negative by construction. For signed integer kinds, a value of
/// <c>MinValue</c> overflows and surfaces as <see cref="OverflowException"/>,
/// matching the underlying primitive's <c>Abs</c> behavior.
/// </para>
/// </remarks>
public sealed class AbsFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "abs";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Returns the absolute value of a numeric input. Result kind matches the input kind.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec(
                    "value",
                    DataKindMatcher.OneOf(
                        DataKind.Int8, DataKind.Int16, DataKind.Int32, DataKind.Int64, DataKind.Int128,
                        DataKind.UInt8, DataKind.UInt16, DataKind.UInt32, DataKind.UInt64, DataKind.UInt128,
                        DataKind.Float16, DataKind.Float32, DataKind.Float64, DataKind.Decimal)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<AbsFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueRef Execute(ReadOnlySpan<ValueRef> arguments, in EvaluationFrame frame)
    {
        ValueRef input = arguments[0];
        if (input.IsNull)
        {
            return ValueRef.Null(input.Kind);
        }

        return input.Kind switch
        {
            DataKind.UInt8 or DataKind.UInt16 or DataKind.UInt32
                or DataKind.UInt64 or DataKind.UInt128 => input,

            DataKind.Int8 => ValueRef.FromInt8(sbyte.Abs(input.AsInt8())),
            DataKind.Int16 => ValueRef.FromInt16(short.Abs(input.AsInt16())),
            DataKind.Int32 => ValueRef.FromInt32(int.Abs(input.AsInt32())),
            DataKind.Int64 => ValueRef.FromInt64(long.Abs(input.AsInt64())),
            DataKind.Int128 => ValueRef.FromInt128(Int128.Abs(input.AsInt128())),
            DataKind.Float16 => ValueRef.FromFloat16(Half.Abs(input.AsFloat16())),
            DataKind.Float32 => ValueRef.FromFloat32(float.Abs(input.AsFloat32())),
            DataKind.Float64 => ValueRef.FromFloat64(double.Abs(input.AsFloat64())),
            DataKind.Decimal => ValueRef.FromDecimal(decimal.Abs(input.AsDecimal())),

            _ => throw new FunctionArgumentException(
                Name,
                $"does not support kind {input.Kind}."),
        };
    }
}
