using System.Numerics;
using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Spatial;

/// <summary>
/// Returns the Euclidean distance between two points of the same dimensionality
/// (Point2D vs. Point2D or Point3D vs. Point3D) as <see cref="DataKind.Float32"/>.
/// Null in either argument propagates to null output. Mixing Point2D with Point3D
/// is rejected at validation time.
/// </summary>
public sealed class DistanceFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "distance";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Spatial;

    /// <inheritdoc />
    public static string Description =>
        "Returns the Euclidean distance between two same-dimension points as Float32.";

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
        FunctionMetadata.Validate<DistanceFunction>(argumentKinds);

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

        float d = a.Kind == DataKind.Point2D
            ? Vector2.Distance(a.AsPoint2D(), b.AsPoint2D())
            : Vector3.Distance(a.AsPoint3D(), b.AsPoint3D());
        return new ValueTask<ValueRef>(ValueRef.FromFloat32(d));
    }
}
