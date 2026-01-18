using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Spatial;

/// <summary>
/// Constructs a <see cref="DataKind.Point2D"/> value from X and Y components.
/// Numeric inputs are widened to <see cref="float"/>. Null in any component
/// propagates to a null Point2D.
/// </summary>
public sealed class Point2DFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "point2d";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Spatial;

    /// <inheritdoc />
    public static string Description =>
        "Constructs a Point2D from X and Y numeric components (single-precision).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("x", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("y", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Point2D)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<Point2DFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Point2D));

        args[0].TryToDouble(out double x);
        args[1].TryToDouble(out double y);
        return new ValueTask<ValueRef>(ValueRef.FromPoint2D((float)x, (float)y));
    }
}
