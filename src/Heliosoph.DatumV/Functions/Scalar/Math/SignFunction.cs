using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Math;

/// <summary>
/// Returns the sign of a numeric input: <c>-1</c>, <c>0</c>, or <c>1</c>. The
/// result kind matches the input kind. Unsigned inputs only ever return
/// <c>0</c> or <c>1</c>. For floating-point inputs, <see cref="double.NaN"/>
/// passes through unchanged. Null input propagates to null output.
/// </summary>
public sealed class SignFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "sign";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Returns the sign of a numeric input (-1, 0, or 1). Result kind matches the input kind.";

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
        FunctionMetadata.Validate<SignFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef input = arguments.Span[0];
        if (input.IsNull)
            return new ValueTask<ValueRef>(ValueRef.Null(input.Kind));

        ValueRef result = input.Kind switch
        {
            DataKind.Int8 => ValueRef.FromInt8((sbyte)System.Math.Sign(input.AsInt8())),
            DataKind.Int16 => ValueRef.FromInt16((short)System.Math.Sign(input.AsInt16())),
            DataKind.Int32 => ValueRef.FromInt32(System.Math.Sign(input.AsInt32())),
            DataKind.Int64 => ValueRef.FromInt64(System.Math.Sign(input.AsInt64())),
            DataKind.Int128 => ValueRef.FromInt128(input.AsInt128() == 0 ? 0 : input.AsInt128() > 0 ? 1 : -1),

            DataKind.UInt8 => ValueRef.FromUInt8(input.AsUInt8() == 0 ? (byte)0 : (byte)1),
            DataKind.UInt16 => ValueRef.FromUInt16(input.AsUInt16() == 0 ? (ushort)0 : (ushort)1),
            DataKind.UInt32 => ValueRef.FromUInt32(input.AsUInt32() == 0u ? 0u : 1u),
            DataKind.UInt64 => ValueRef.FromUInt64(input.AsUInt64() == 0ul ? 0ul : 1ul),
            DataKind.UInt128 => ValueRef.FromUInt128(input.AsUInt128() == (UInt128)0 ? (UInt128)0 : (UInt128)1),

            DataKind.Float16 => SignFloat16(input.AsFloat16()),
            DataKind.Float32 => SignFloat32(input.AsFloat32()),
            DataKind.Float64 => SignFloat64(input.AsFloat64()),
            DataKind.Decimal => ValueRef.FromDecimal(System.Math.Sign(input.AsDecimal())),

            _ => throw new FunctionArgumentException(Name, $"does not support kind {input.Kind}."),
        };
        return new ValueTask<ValueRef>(result);
    }

    private static ValueRef SignFloat16(Half value)
    {
        if (Half.IsNaN(value))
            return ValueRef.FromFloat16(value);
        float f = (float)value;
        return ValueRef.FromFloat16((Half)(f == 0f ? 0f : f > 0f ? 1f : -1f));
    }

    private static ValueRef SignFloat32(float value)
    {
        if (float.IsNaN(value))
            return ValueRef.FromFloat32(value);
        return ValueRef.FromFloat32(value == 0f ? 0f : value > 0f ? 1f : -1f);
    }

    private static ValueRef SignFloat64(double value)
    {
        if (double.IsNaN(value))
            return ValueRef.FromFloat64(value);
        return ValueRef.FromFloat64(value == 0.0 ? 0.0 : value > 0.0 ? 1.0 : -1.0);
    }
}
