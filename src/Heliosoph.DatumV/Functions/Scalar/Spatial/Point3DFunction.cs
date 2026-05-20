using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Spatial;

/// <summary>
/// Constructs a <see cref="DataKind.Point3D"/> value from X, Y, Z components.
/// Numeric inputs are widened to <see cref="float"/>. Null in any component
/// propagates to a null Point3D.
/// </summary>
public sealed class Point3DFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "point3d";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Spatial;

    /// <inheritdoc />
    public static string Description =>
        "Constructs a Point3D from X, Y, Z numeric components (single-precision).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("x", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("y", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("z", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Point3D)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<Point3DFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull)
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Point3D));

        args[0].TryToDouble(out double x);
        args[1].TryToDouble(out double y);
        args[2].TryToDouble(out double z);
        return new ValueTask<ValueRef>(ValueRef.FromPoint3D((float)x, (float)y, (float)z));
    }
}
