using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Math;

/// <summary>
/// Returns true when the floating-point argument is NaN, false otherwise.
/// PostgreSQL-conformant (PG names the function <c>isnan</c> without an
/// underscore; both names are registered). Null input propagates to null
/// output.
/// </summary>
public sealed class IsNanFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "is_nan";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Utility;

    /// <inheritdoc />
    public static string Description =>
        "Returns true when the floating-point argument is NaN, false otherwise.";

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
        FunctionMetadata.Validate<IsNanFunction>(argumentKinds);

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
            DataKind.Float16 => Half.IsNaN(value.AsFloat16()),
            DataKind.Float32 => float.IsNaN(value.AsFloat32()),
            DataKind.Float64 => double.IsNaN(value.AsFloat64()),
            _ => false,
        };
        return new ValueTask<ValueRef>(ValueRef.FromBoolean(result));
    }
}
