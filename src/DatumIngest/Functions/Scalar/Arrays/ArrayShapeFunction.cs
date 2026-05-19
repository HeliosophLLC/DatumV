using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Arrays;

/// <summary>
/// Returns the declared shape of a multi-dimensional typed-array value as an
/// <c>Array&lt;Int32&gt;</c> of per-dimension sizes. For flat (1-D) arrays the
/// shape is a single-element <c>[length]</c> array; for multi-dim values the
/// shape reflects the dims carried in the value's prefix
/// (<see cref="DataValue.GetShape"/>). Null arrays yield a null result.
/// </summary>
public sealed class ArrayShapeFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "array_shape";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Array;

    /// <inheritdoc />
    public static string Description =>
        "Returns the declared per-dimension sizes of a typed array as an "
        + "Int32 array. Multi-dim arrays (e.g. Array<Float32>(2,3)) return "
        + "[2,3]; 1-D arrays return [length].";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("array", DataKindMatcher.Any, IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Int32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ArrayShapeFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];

        if (arg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.Int32));
        }

        DataValue value = arg.ToDataValue(frame.Source);
        if (value.IsMultiDim)
        {
            ReadOnlySpan<int> shape = value.GetShape(frame.Source, frame.SidecarRegistry);
            return new ValueTask<ValueRef>(
                ValueRef.FromPrimitiveArray(shape.ToArray(), DataKind.Int32));
        }

        // Flat 1-D array — synthesize a single-element shape from the element count.
        int length = arg.GetArrayLength();
        return new ValueTask<ValueRef>(
            ValueRef.FromPrimitiveArray(new[] { length }, DataKind.Int32));
    }
}
