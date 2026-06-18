using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Math;

/// <summary>
/// Returns true when the floating-point argument is ±infinity, false
/// otherwise (including NaN). Companion to <see cref="IsFiniteFunction"/>;
/// note that <c>is_infinite(NaN) = false</c> — NaN is neither finite nor
/// infinite, so the two predicates are not strict complements. Null input
/// propagates to null output.
/// </summary>
public sealed class IsInfiniteFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "is_infinite";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Utility;

    /// <inheritdoc />
    public static string Description =>
        "Returns true when the floating-point argument is ±infinity, false otherwise (including NaN).";

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
        FunctionMetadata.Validate<IsInfiniteFunction>(argumentKinds);

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
            DataKind.Float16 => Half.IsInfinity(value.AsFloat16()),
            DataKind.Float32 => float.IsInfinity(value.AsFloat32()),
            DataKind.Float64 => double.IsInfinity(value.AsFloat64()),
            _ => false,
        };
        return new ValueTask<ValueRef>(ValueRef.FromBoolean(result));
    }
}
