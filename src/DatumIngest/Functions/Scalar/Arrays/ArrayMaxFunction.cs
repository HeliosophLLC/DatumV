using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Arrays;

/// <summary>
/// Returns the maximum element of a typed array using the element kind's
/// natural ordering. See <see cref="ArrayMinFunction"/> for the supported
/// element kinds — media kinds, <c>String</c>, <c>Struct</c>, <c>Json</c>,
/// and <c>Point2D</c>/<c>Point3D</c> are rejected.
/// </summary>
public sealed class ArrayMaxFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "array_max";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Array;

    /// <inheritdoc />
    public static string Description =>
        "Returns the maximum element of a typed array. Supports numeric, "
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
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ArrayMaxFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken) =>
        new(ArrayMinMaxCore.Execute(arguments.Span[0], pickSmaller: false, frame, Name));
}
