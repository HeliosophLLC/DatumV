using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Math;

/// <summary>
/// Returns the negation (-x) of a numeric input. The result kind matches the
/// input kind. Unsigned integer kinds are rejected because their negation does
/// not fit in the same kind. For signed integers, negating <c>MinValue</c>
/// overflows and surfaces as <see cref="OverflowException"/>. Null input
/// propagates to null output.
/// </summary>
public sealed class NegateFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "negate";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Returns the negation (-x) of a numeric input. Result kind matches the input kind.";

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
                        DataKind.Float16, DataKind.Float32, DataKind.Float64, DataKind.Decimal)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<NegateFunction>(argumentKinds);

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
            DataKind.Int8 => ValueRef.FromInt8(checked((sbyte)-input.AsInt8())),
            DataKind.Int16 => ValueRef.FromInt16(checked((short)-input.AsInt16())),
            DataKind.Int32 => ValueRef.FromInt32(checked(-input.AsInt32())),
            DataKind.Int64 => ValueRef.FromInt64(checked(-input.AsInt64())),
            DataKind.Int128 => ValueRef.FromInt128(checked(-input.AsInt128())),
            DataKind.Float16 => ValueRef.FromFloat16((Half)(-(float)input.AsFloat16())),
            DataKind.Float32 => ValueRef.FromFloat32(-input.AsFloat32()),
            DataKind.Float64 => ValueRef.FromFloat64(-input.AsFloat64()),
            DataKind.Decimal => ValueRef.FromDecimal(-input.AsDecimal()),

            _ => throw new FunctionArgumentException(Name, $"does not support kind {input.Kind}."),
        };
        return new ValueTask<ValueRef>(result);
    }
}
