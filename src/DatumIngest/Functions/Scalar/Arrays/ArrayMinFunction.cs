using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Arrays;

/// <summary>
/// Returns the minimum element of a typed array using the element kind's
/// natural ordering. Supports the fixed-width comparable kinds: every
/// integer and float width, <c>Decimal</c>, <c>Boolean</c>, the temporal
/// kinds (<c>Date</c>, <c>Time</c>, <c>Duration</c>, <c>Timestamp</c>,
/// <c>TimestampTz</c>), and <c>Uuid</c>. Multi-dim arrays reduce across the
/// whole tensor (the result is the global minimum). Empty and null arrays
/// return a typed null. Media kinds (<c>Image</c>, <c>Audio</c>, <c>Video</c>,
/// <c>PointCloud</c>, <c>Mesh</c>), <c>String</c>, <c>Struct</c>, <c>Json</c>,
/// and <c>Point2D</c>/<c>Point3D</c> are rejected — none of them carry a
/// natural scalar ordering.
/// </summary>
public sealed class ArrayMinFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "array_min";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Array;

    /// <inheritdoc />
    public static string Description =>
        "Returns the minimum element of a typed array. Supports numeric, "
        + "Boolean, temporal (Date/Time/Duration/Timestamp/TimestampTz), "
        + "and Uuid element kinds. Multi-dim arrays reduce across the whole "
        + "tensor. Empty and null arrays return null.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("array", DataKindMatcher.Any, IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            // Result kind = element kind (drops the array dimension).
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ArrayMinFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken) =>
        new(ArrayMinMaxCore.Execute(arguments.Span[0], pickSmaller: true, frame, Name));
}
