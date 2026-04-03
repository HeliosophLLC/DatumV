using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Arrays;

/// <summary>
/// Reads a single element from a typed array by positional indices. The
/// number of trailing index arguments must equal the array's dimensionality:
/// <c>array_get(arr, i)</c> for a 1-D array, <c>array_get(arr, y, x)</c> for
/// a 2-D array, and so on. Indices are 1-based, row-major (PostgreSQL semantics).
/// Out-of-range indices yield a typed null of the element kind. Provided as a
/// runtime substitute for the <c>arr[y, x]</c> bracket syntax so multi-dim
/// arrays can be exercised by SQL today.
/// </summary>
public sealed class ArrayGetFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "array_get";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Array;

    /// <inheritdoc />
    public static string Description =>
        "Reads a single element from a typed array by positional indices. "
        + "The number of indices must equal the array's ndim (1 for flat, "
        + "ndim for multi-dim). Returns a typed null on out-of-range.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("array", DataKindMatcher.Any, IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: new VariadicSpec(
                "indices",
                DataKindMatcher.Family(DataKindFamily.NumericScalar),
                MinOccurrences: 1),
            // Result kind = element kind of the array arg (drops the array dimension).
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ArrayGetFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef arrayArg = args[0];
        if (arrayArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(arrayArg.Kind));
        }

        DataValue source = arrayArg.ToDataValue(frame.Source);
        int indexCount = args.Length - 1;

        int flatOffset;
        if (source.IsMultiDim)
        {
            ReadOnlySpan<int> shape = source.GetShape(frame.Source, frame.SidecarRegistry);
            if (indexCount != shape.Length)
            {
                throw new FunctionArgumentException(Name,
                    $"array is {shape.Length}-dimensional but {indexCount} " +
                    $"{(indexCount == 1 ? "index was" : "indices were")} supplied.");
            }
            flatOffset = ComputeFlatOffset(args[1..], shape);
        }
        else
        {
            // Flat array — exactly one index expected.
            if (indexCount != 1)
            {
                throw new FunctionArgumentException(Name,
                    $"array is 1-dimensional but {indexCount} indices were supplied.");
            }
            // PostgreSQL-style 1-based index → 0-based internal offset.
            flatOffset = args[1].ToInt32() - 1;
        }

        return new ValueTask<ValueRef>(ReadElement(source, flatOffset, frame));
    }

    /// <summary>
    /// Computes a flat row-major offset from a span of <paramref name="indices"/>
    /// and the array's <paramref name="shape"/>. Throws when any index is out
    /// of range for its dimension — the caller can rely on the returned offset
    /// being valid for typed-span indexing.
    /// </summary>
    private static int ComputeFlatOffset(ReadOnlySpan<ValueRef> indices, ReadOnlySpan<int> shape)
    {
        long offset = 0;
        for (int i = 0; i < indices.Length; i++)
        {
            // PostgreSQL-style 1-based indices → 0-based internal offset.
            int userIndex = indices[i].ToInt32();
            int dimIndex = userIndex - 1;
            int dimSize = shape[i];
            if (dimIndex < 0 || dimIndex >= dimSize)
            {
                throw new FunctionArgumentException(Name,
                    $"index {i} out of range: {userIndex} not in [1, {dimSize}].");
            }
            offset = offset * dimSize + dimIndex;
        }
        return (int)offset;
    }

    /// <summary>
    /// Reads the element at <paramref name="position"/> from a typed-array
    /// <paramref name="source"/>, returning a typed null on out-of-range.
    /// Mirrors <c>ExpressionEvaluator.ReadFixedWidthArrayElement</c>'s dispatch
    /// table — kept local rather than refactored into a shared helper because
    /// this function is the only second caller and the path will collapse once
    /// the <c>arr[y, x]</c> bracket syntax lands and folds back into the
    /// evaluator's index-access path.
    /// </summary>
    private static ValueRef ReadElement(DataValue source, int position, EvaluationFrame frame)
    {
        DataKind kind = source.Kind;
        return kind switch
        {
            DataKind.Int8 => Read<sbyte>(source, position, frame, x => ValueRef.FromInt8(x), DataKind.Int8),
            DataKind.UInt8 => Read<byte>(source, position, frame, ValueRef.FromUInt8, DataKind.UInt8),
            DataKind.Int16 => Read<short>(source, position, frame, ValueRef.FromInt16, DataKind.Int16),
            DataKind.UInt16 => Read<ushort>(source, position, frame, ValueRef.FromUInt16, DataKind.UInt16),
            DataKind.Float16 => Read<Half>(source, position, frame, ValueRef.FromFloat16, DataKind.Float16),
            DataKind.Int32 => Read<int>(source, position, frame, ValueRef.FromInt32, DataKind.Int32),
            DataKind.UInt32 => Read<uint>(source, position, frame, ValueRef.FromUInt32, DataKind.UInt32),
            DataKind.Float32 => Read<float>(source, position, frame, ValueRef.FromFloat32, DataKind.Float32),
            DataKind.Int64 => Read<long>(source, position, frame, ValueRef.FromInt64, DataKind.Int64),
            DataKind.UInt64 => Read<ulong>(source, position, frame, ValueRef.FromUInt64, DataKind.UInt64),
            DataKind.Float64 => Read<double>(source, position, frame, ValueRef.FromFloat64, DataKind.Float64),
            _ => throw new FunctionArgumentException(Name,
                $"array_get does not yet support element kind {kind}."),
        };
    }

    private static ValueRef Read<T>(
        DataValue source, int position, EvaluationFrame frame,
        Func<T, ValueRef> wrap, DataKind nullKind) where T : unmanaged
    {
        ReadOnlySpan<T> elements = source.AsArraySpan<T>(frame.Source, frame.SidecarRegistry);
        if (position < 0 || position >= elements.Length)
        {
            return ValueRef.Null(nullKind);
        }
        return wrap(elements[position]);
    }
}
