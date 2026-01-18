using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Spatial;

/// <summary>
/// Returns the Z component of a <see cref="DataKind.Point3D"/> as
/// <see cref="DataKind.Float32"/>. Null input propagates to null output.
/// Point2D has no Z component and is not accepted.
/// </summary>
public sealed class PointZFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "point_z";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Spatial;

    /// <inheritdoc />
    public static string Description =>
        "Returns the Z component of a Point3D as Float32.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("point", DataKindMatcher.Exact(DataKind.Point3D)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PointZFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef p = arguments.Span[0];
        if (p.IsNull)
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32));

        return new ValueTask<ValueRef>(ValueRef.FromFloat32(p.AsPoint3D().Z));
    }
}
