using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Math;

/// <summary>
/// Returns the smallest integer not less than the input value.
/// Integer kinds are returned unchanged. The result kind matches the input kind.
/// Null input propagates to null output.
/// </summary>
/// <remarks>
/// Register alias <c>ceiling</c> via <see cref="FunctionRegistry.RegisterScalarAlias{T}"/>.
/// </remarks>
public sealed class CeilFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "ceil";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Returns the smallest integer not less than the input. Result kind matches the input kind.";

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
        FunctionMetadata.Validate<CeilFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef input = args[0];
        if (input.IsNull)
            return new ValueTask<ValueRef>(ValueRef.Null(input.Kind));

        ValueRef result = input.Kind switch
        {
            DataKind.Int8 or DataKind.Int16 or DataKind.Int32 or DataKind.Int64 or DataKind.Int128 or
            DataKind.UInt8 or DataKind.UInt16 or DataKind.UInt32 or DataKind.UInt64 or DataKind.UInt128
                => input,

            DataKind.Float16 => ValueRef.FromFloat16(
                (Half)MathF.Ceiling((float)input.AsFloat16())),
            DataKind.Float32 => ValueRef.FromFloat32(
                MathF.Ceiling(input.AsFloat32())),
            DataKind.Float64 => ValueRef.FromFloat64(
                System.Math.Ceiling(input.AsFloat64())),
            DataKind.Decimal => ValueRef.FromDecimal(
                System.Math.Ceiling(input.AsDecimal())),

            _ => throw new FunctionArgumentException(Name, $"does not support kind {input.Kind}."),
        };
        return new ValueTask<ValueRef>(result);
    }
}
