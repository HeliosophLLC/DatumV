using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Math;

/// <summary>
/// Returns true when the floating-point argument is a finite number
/// (not NaN, not ±infinity); false otherwise. PostgreSQL-conformant
/// (PG names it <c>isfinite</c>; both names are registered). Null input
/// propagates to null output.
/// </summary>
public sealed class IsFiniteFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "is_finite";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Utility;

    /// <inheritdoc />
    public static string Description =>
        "Returns true when the floating-point argument is finite (not NaN, not ±infinity).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec(
                    "value",
                    DataKindMatcher.OneOf(DataKind.Float16, DataKind.Float32, DataKind.Float64)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Boolean)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<IsFiniteFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef value = arguments.Span[0];
        if (value.IsNull) return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Boolean));

        bool result = value.Kind switch
        {
            DataKind.Float16 => Half.IsFinite(value.AsFloat16()),
            DataKind.Float32 => float.IsFinite(value.AsFloat32()),
            DataKind.Float64 => double.IsFinite(value.AsFloat64()),
            _ => true,
        };
        return new ValueTask<ValueRef>(ValueRef.FromBoolean(result));
    }
}
