using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Spatial;

/// <summary>
/// Returns the X component of a <see cref="DataKind.Point2D"/> or
/// <see cref="DataKind.Point3D"/> as <see cref="DataKind.Float32"/>.
/// Null input propagates to null output.
/// </summary>
public sealed class PointXFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "point_x";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Spatial;

    /// <inheritdoc />
    public static string Description =>
        "Returns the X component of a Point2D or Point3D as Float32.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("point", DataKindMatcher.OneOf(DataKind.Point2D, DataKind.Point3D)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PointXFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef p = arguments.Span[0];
        if (p.IsNull)
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32));

        float x = p.Kind == DataKind.Point2D ? p.AsPoint2D().X : p.AsPoint3D().X;
        return new ValueTask<ValueRef>(ValueRef.FromFloat32(x));
    }
}
