using System.Numerics;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Spatial;

/// <summary>
/// Returns the squared Euclidean distance between two same-dimension points
/// as <see cref="DataKind.Float32"/>. Avoids the square root of <c>distance()</c>;
/// suitable for KNN-style ranking and threshold checks where the absolute
/// distance is not needed. Null in either argument propagates to null.
/// </summary>
public sealed class DistanceSqFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "distance_sq";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Spatial;

    /// <inheritdoc />
    public static string Description =>
        "Returns the squared Euclidean distance between two same-dimension points as Float32.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("a", DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("b", DataKindMatcher.Exact(DataKind.Point2D)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("a", DataKindMatcher.Exact(DataKind.Point3D)),
                new ParameterSpec("b", DataKindMatcher.Exact(DataKind.Point3D)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DistanceSqFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef a = args[0];
        ValueRef b = args[1];
        if (a.IsNull || b.IsNull)
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32));

        float d2 = a.Kind == DataKind.Point2D
            ? Vector2.DistanceSquared(a.AsPoint2D(), b.AsPoint2D())
            : Vector3.DistanceSquared(a.AsPoint3D(), b.AsPoint3D());
        return new ValueTask<ValueRef>(ValueRef.FromFloat32(d2));
    }
}
