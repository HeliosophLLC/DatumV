using System.Numerics;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Arrays;

/// <summary>
/// <c>array_slice(arr, start, length) → Array&lt;T&gt;</c>. Returns a
/// contiguous window of the source array starting at the 1-based
/// <c>start</c> index and spanning <c>length</c> elements.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Indexing.</strong> 1-based to match the existing
/// <c>array_get</c> convention and PostgreSQL's array semantics. A
/// <c>start</c> of 1 is the first element; <c>length</c> of N takes
/// the next N. Use cases include per-frame inference loops (Silero
/// VAD, custom streaming pipelines), token-list trimming after
/// <c>decode_seq2seq</c>, and chunked embedding bucket tests.
/// </para>
/// <para>
/// <strong>Clamp at the right edge.</strong> When <c>start + length</c>
/// extends past the array, the result is truncated to whatever lies
/// in range — matches PostgreSQL's <c>arr[start:end]</c> slice
/// behaviour and lets procedural loops handle ragged trailing frames
/// without an explicit length check on every iteration. An out-of-
/// range <c>start</c> (past the array end) yields an empty array
/// rather than throwing.
/// </para>
/// <para>
/// <strong>Hard errors.</strong> <c>start &lt; 1</c> and
/// <c>length &lt; 0</c> raise — both indicate authoring bugs that the
/// clamp behaviour would silently hide.
/// </para>
/// <para>
/// <strong>1-D only.</strong> Multi-dim arrays raise a clear error
/// rather than silently slicing the flat row-major buffer. A future
/// axis-aware slicer can land separately when a real consumer needs
/// it; today's call sites all want 1-D windowing.
/// </para>
/// </remarks>
public sealed class ArraySliceFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "array_slice";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Array;

    /// <inheritdoc />
    public static string Description =>
        "Returns a contiguous window of the source array starting at the "
        + "1-based `start` index and spanning `length` elements. PostgreSQL-"
        + "style clamp at the right edge — over-length requests truncate to "
        + "whatever fits. 1-D arrays only; multi-dim raises. Supports every "
        + "fixed-width primitive element kind.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("array",  DataKindMatcher.Any, IsArray: ArrayMatch.Array),
                new ParameterSpec("start",  DataKindMatcher.Family(DataKindFamily.IntegerFamily), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("length", DataKindMatcher.Family(DataKindFamily.IntegerFamily), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            // Result is an array of the same element kind as the source.
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ArraySliceFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef arrayArg = args[0];
        if (arrayArg.IsNull || args[1].IsNull || args[2].IsNull)
        {
            // Typed-null array columns surface with IsArray=false but the
            // element Kind preserved on the carrier — fall back to Kind
            // rather than ArrayElementKind, which would throw.
            DataKind elementKind = arrayArg.IsArray ? arrayArg.ArrayElementKind : arrayArg.Kind;
            return new ValueTask<ValueRef>(ValueRef.NullArray(elementKind));
        }

        int start = args[1].ToInt32();
        int length = args[2].ToInt32();
        if (start < 1)
        {
            throw new FunctionArgumentException(Name,
                $"start must be ≥ 1 (PostgreSQL convention); got {start}.");
        }
        if (length < 0)
        {
            throw new FunctionArgumentException(Name,
                $"length must be ≥ 0; got {length}.");
        }

        // Reject multi-dim explicitly so we don't silently slice the
        // flat row-major buffer of a tensor. Check off the ValueRef
        // directly — materialising the full input via ToDataValue just
        // to read IsMultiDim was forcing an O(input-size) arena copy on
        // every call (256 MB × 200+ iterations in the SAM body's loop).
        if (arrayArg.IsMultiDim)
        {
            DataValue source = arrayArg.ToDataValue(frame.Source);
            ReadOnlySpan<int> shape = source.GetShape(frame.Source, frame.SidecarRegistry);
            throw new FunctionArgumentException(Name,
                "array_slice operates on 1-D arrays only; got shape "
                + $"[{string.Join(", ", shape.ToArray())}]. Flatten with the "
                + "appropriate axis primitive before slicing.");
        }

        int sourceLength = arrayArg.GetArrayLength();
        int startIndex = start - 1;                                  // 0-based
        if (startIndex >= sourceLength || length == 0)
        {
            return new ValueTask<ValueRef>(EmptyArrayOf(arrayArg.ArrayElementKind));
        }
        int clampedLength = System.Math.Min(length, sourceLength - startIndex);

        return new ValueTask<ValueRef>(arrayArg.ArrayElementKind switch
        {
            DataKind.Boolean     => SliceBoolean(arrayArg, startIndex, clampedLength),
            DataKind.UInt8       => SlicePrimitive<byte>(arrayArg, startIndex, clampedLength, DataKind.UInt8),
            DataKind.Int8        => SlicePrimitive<sbyte>(arrayArg, startIndex, clampedLength, DataKind.Int8),
            DataKind.UInt16      => SlicePrimitive<ushort>(arrayArg, startIndex, clampedLength, DataKind.UInt16),
            DataKind.Int16       => SlicePrimitive<short>(arrayArg, startIndex, clampedLength, DataKind.Int16),
            DataKind.Float16     => SlicePrimitive<Half>(arrayArg, startIndex, clampedLength, DataKind.Float16),
            DataKind.UInt32      => SlicePrimitive<uint>(arrayArg, startIndex, clampedLength, DataKind.UInt32),
            DataKind.Int32       => SlicePrimitive<int>(arrayArg, startIndex, clampedLength, DataKind.Int32),
            DataKind.Float32     => SlicePrimitive<float>(arrayArg, startIndex, clampedLength, DataKind.Float32),
            DataKind.Date        => SlicePrimitive<int>(arrayArg, startIndex, clampedLength, DataKind.Date),
            DataKind.UInt64      => SlicePrimitive<ulong>(arrayArg, startIndex, clampedLength, DataKind.UInt64),
            DataKind.Int64       => SlicePrimitive<long>(arrayArg, startIndex, clampedLength, DataKind.Int64),
            DataKind.Float64     => SlicePrimitive<double>(arrayArg, startIndex, clampedLength, DataKind.Float64),
            DataKind.Time        => SlicePrimitive<long>(arrayArg, startIndex, clampedLength, DataKind.Time),
            DataKind.Duration    => SlicePrimitive<long>(arrayArg, startIndex, clampedLength, DataKind.Duration),
            DataKind.Timestamp   => SlicePrimitive<long>(arrayArg, startIndex, clampedLength, DataKind.Timestamp),
            DataKind.TimestampTz => SlicePrimitive<long>(arrayArg, startIndex, clampedLength, DataKind.TimestampTz),
            DataKind.Decimal     => SlicePrimitive<decimal>(arrayArg, startIndex, clampedLength, DataKind.Decimal),
            DataKind.UInt128     => SlicePrimitive<UInt128>(arrayArg, startIndex, clampedLength, DataKind.UInt128),
            DataKind.Int128      => SlicePrimitive<Int128>(arrayArg, startIndex, clampedLength, DataKind.Int128),
            DataKind.Uuid        => SlicePrimitive<Guid>(arrayArg, startIndex, clampedLength, DataKind.Uuid),
            DataKind.Point2D     => SlicePrimitive<Vector2>(arrayArg, startIndex, clampedLength, DataKind.Point2D),

            // Reference-array kinds (carried as ValueRef[] / string[] / blob
            // payloads, not as a packed typed array). The slicing logic is
            // straightforward but needs its own path; until a real consumer
            // shows up, reject with a clear error rather than half-wire it.
            DataKind.String or DataKind.Struct
                or DataKind.Image or DataKind.Audio
                or DataKind.Video or DataKind.Json
                => throw new FunctionArgumentException(Name,
                    $"array_slice does not yet support Array<{arrayArg.ArrayElementKind}>: "
                    + "reference-array kinds use a ValueRef[] payload shape "
                    + "that needs a separate slicing path."),

            DataKind.Unknown or DataKind.Type
                => throw new FunctionArgumentException(Name,
                    $"array_slice does not support element kind {arrayArg.ArrayElementKind}."),

            _ => throw new FunctionArgumentException(Name,
                $"array_slice does not support element kind {arrayArg.ArrayElementKind}."),
        });
    }

    private static ValueRef EmptyArrayOf(DataKind kind) => kind switch
    {
        DataKind.Boolean     => ValueRef.FromPrimitiveArray(Array.Empty<byte>(), DataKind.Boolean),
        DataKind.UInt8       => ValueRef.FromPrimitiveArray(Array.Empty<byte>(), DataKind.UInt8),
        DataKind.Int8        => ValueRef.FromPrimitiveArray(Array.Empty<sbyte>(), DataKind.Int8),
        DataKind.UInt16      => ValueRef.FromPrimitiveArray(Array.Empty<ushort>(), DataKind.UInt16),
        DataKind.Int16       => ValueRef.FromPrimitiveArray(Array.Empty<short>(), DataKind.Int16),
        DataKind.Float16     => ValueRef.FromPrimitiveArray(Array.Empty<Half>(), DataKind.Float16),
        DataKind.UInt32      => ValueRef.FromPrimitiveArray(Array.Empty<uint>(), DataKind.UInt32),
        DataKind.Int32       => ValueRef.FromPrimitiveArray(Array.Empty<int>(), DataKind.Int32),
        DataKind.Float32     => ValueRef.FromPrimitiveArray(Array.Empty<float>(), DataKind.Float32),
        DataKind.Date        => ValueRef.FromPrimitiveArray(Array.Empty<int>(), DataKind.Date),
        DataKind.UInt64      => ValueRef.FromPrimitiveArray(Array.Empty<ulong>(), DataKind.UInt64),
        DataKind.Int64       => ValueRef.FromPrimitiveArray(Array.Empty<long>(), DataKind.Int64),
        DataKind.Float64     => ValueRef.FromPrimitiveArray(Array.Empty<double>(), DataKind.Float64),
        DataKind.Time        => ValueRef.FromPrimitiveArray(Array.Empty<long>(), DataKind.Time),
        DataKind.Duration    => ValueRef.FromPrimitiveArray(Array.Empty<long>(), DataKind.Duration),
        DataKind.Timestamp   => ValueRef.FromPrimitiveArray(Array.Empty<long>(), DataKind.Timestamp),
        DataKind.TimestampTz => ValueRef.FromPrimitiveArray(Array.Empty<long>(), DataKind.TimestampTz),
        DataKind.Decimal     => ValueRef.FromPrimitiveArray(Array.Empty<decimal>(), DataKind.Decimal),
        DataKind.UInt128     => ValueRef.FromPrimitiveArray(Array.Empty<UInt128>(), DataKind.UInt128),
        DataKind.Int128      => ValueRef.FromPrimitiveArray(Array.Empty<Int128>(), DataKind.Int128),
        DataKind.Uuid        => ValueRef.FromPrimitiveArray(Array.Empty<Guid>(), DataKind.Uuid),
        DataKind.Point2D     => ValueRef.FromPrimitiveArray(Array.Empty<Vector2>(), DataKind.Point2D),
        _ => ValueRef.NullArray(kind),
    };

    private static ValueRef SliceBoolean(ValueRef arg, int startIndex, int length)
    {
        byte[] source = arg.Materialized switch
        {
            byte[] bytes => bytes,
            bool[] bools => BoolsToBytes(bools),
            _ => ToTypedArray<byte>(arg),
        };
        byte[] result = new byte[length];
        if (length > 0)
            Array.Copy(source, startIndex, result, 0, length);
        return ValueRef.FromPrimitiveArray(result, DataKind.Boolean);
    }

    private static byte[] BoolsToBytes(bool[] bools)
    {
        byte[] result = new byte[bools.Length];
        for (int i = 0; i < bools.Length; i++)
            result[i] = bools[i] ? (byte)1 : (byte)0;
        return result;
    }

    private static ValueRef SlicePrimitive<T>(ValueRef arg, int startIndex, int length, DataKind kind)
        where T : unmanaged
    {
        T[] source = arg.Materialized as T[] ?? ToTypedArray<T>(arg);
        T[] result = new T[length];
        if (length > 0)
            Array.Copy(source, startIndex, result, 0, length);
        return ValueRef.FromPrimitiveArray(result, kind);
    }

    /// <summary>
    /// Pulls a typed <c>T[]</c> out of a ValueRef whose underlying payload
    /// isn't already the canonical typed array (e.g. arrays built by SQL
    /// array literals, where the materialised payload is a
    /// <see cref="ValueRef"/>[]).
    /// </summary>
    private static T[] ToTypedArray<T>(ValueRef arg) where T : unmanaged
    {
        ReadOnlySpan<ValueRef> elements = arg.GetArrayElements();
        T[] copied = new T[elements.Length];
        for (int i = 0; i < elements.Length; i++)
        {
            copied[i] = elements[i] switch
            {
                _ when typeof(T) == typeof(float) && elements[i].TryToFloat(out float f)
                    => (T)(object)f,
                _ when typeof(T) == typeof(int) && elements[i].TryToInt32(out int i32)
                    => (T)(object)i32,
                _ when typeof(T) == typeof(long) && elements[i].TryToInt64(out long i64)
                    => (T)(object)i64,
                _ => throw new FunctionArgumentException(Name,
                    $"array element [{i}] of kind {elements[i].Kind} "
                    + $"is not coercible to {typeof(T).Name}."),
            };
        }
        return copied;
    }
}
