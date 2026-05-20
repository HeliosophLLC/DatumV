using System.Numerics;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Arrays;

/// <summary>
/// Concatenates two arrays of the same element kind, returning a new
/// array whose contents are the first argument followed by the second.
/// The canonical use is stitching tensor segments
/// together before passing them to a model — e.g. Florence-2's encoder
/// input is the concatenation of visual embeddings (from the vision
/// encoder) and prompt token embeddings (from <c>embed_tokens</c>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Element-kind rule.</strong> Both arrays must share an element
/// kind; mixed kinds raise a clear error rather than coercing silently.
/// Supported element kinds are the fixed-width primitives backed by
/// <see cref="ValueRef.FromPrimitiveArray{T}"/>: every integer width
/// (signed and unsigned, 8/16/32/64/128), every float width
/// (<c>Float16</c>/<c>Float32</c>/<c>Float64</c>/<c>Decimal</c>),
/// <c>Boolean</c>, the temporal kinds (<c>Date</c>, <c>Time</c>,
/// <c>Duration</c>, <c>Timestamp</c>, <c>TimestampTz</c>), <c>Uuid</c>,
/// and <c>Point2D</c>.
/// </para>
/// <para>
/// <strong>Unsupported kinds.</strong> Composite/blob kinds
/// (<c>Struct</c>, <c>String</c>, <c>Image</c>, <c>Audio</c>,
/// <c>Video</c>, <c>Json</c>) and the meta kinds (<c>Unknown</c>,
/// <c>Type</c>) are rejected: arrays of those kinds use a
/// reference-array payload shape that this function doesn't yet stitch.
/// </para>
/// <para>
/// <strong>Nulls.</strong> A null array is treated as an empty array;
/// <c>array_concat(NULL, x)</c> returns <c>x</c> and vice versa. Both
/// null yields a null array. Models often produce optionally-empty
/// prefix segments (e.g. Florence-2's "no task prefix" mode); treating
/// null as identity keeps the SQL bodies free of CASE branches.
/// </para>
/// </remarks>
public sealed class ArrayConcatFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "array_concat";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Array;

    /// <inheritdoc />
    public static string Description =>
        "Concatenates two arrays of the same element kind. NULL arrays "
        + "are treated as empty (identity). Supports every fixed-width "
        + "primitive element kind (integers, floats, Decimal, Boolean, "
        + "Date, Time, Duration, Uuid, Point2D). DateTime, composite "
        + "kinds (Struct/String/Image/Audio/Video/Json) and meta kinds "
        + "(Unknown/Type) are rejected.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("a", DataKindMatcher.Any, IsArray: ArrayMatch.Array),
                new ParameterSpec("b", DataKindMatcher.Any, IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            // Result is an array of the same element kind as `a`. The matcher
            // already requires both args to be arrays; the runtime check
            // below ensures kind compatibility.
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ArrayConcatFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef a = args[0];
        ValueRef b = args[1];

        // NULL identity rule. Both null → null. One null → the other.
        if (a.IsNull && b.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(a.ArrayElementKind));
        }
        if (a.IsNull) return new ValueTask<ValueRef>(b);
        if (b.IsNull) return new ValueTask<ValueRef>(a);

        if (a.ArrayElementKind != b.ArrayElementKind)
        {
            throw new FunctionArgumentException(Name,
                $"both arrays must have the same element kind; got "
                + $"{a.ArrayElementKind}[] and {b.ArrayElementKind}[].");
        }

        // One fast path per fixed-width primitive kind. Each takes the
        // materialised typed array, allocates one result array, blocks
        // copies in. The T per kind matches DataValue.ScalarByteSize and
        // the canonical mapping in ValueRef.BuildPrimitiveArray.
        return new ValueTask<ValueRef>(a.ArrayElementKind switch
        {
            DataKind.Boolean  => ConcatBoolean(a, b),
            DataKind.UInt8    => ConcatPrimitive<byte>(a, b, DataKind.UInt8),
            DataKind.Int8     => ConcatPrimitive<sbyte>(a, b, DataKind.Int8),
            DataKind.UInt16   => ConcatPrimitive<ushort>(a, b, DataKind.UInt16),
            DataKind.Int16    => ConcatPrimitive<short>(a, b, DataKind.Int16),
            DataKind.Float16  => ConcatPrimitive<Half>(a, b, DataKind.Float16),
            DataKind.UInt32   => ConcatPrimitive<uint>(a, b, DataKind.UInt32),
            DataKind.Int32    => ConcatPrimitive<int>(a, b, DataKind.Int32),
            DataKind.Float32  => ConcatPrimitive<float>(a, b, DataKind.Float32),
            DataKind.Date     => ConcatPrimitive<int>(a, b, DataKind.Date),
            DataKind.UInt64   => ConcatPrimitive<ulong>(a, b, DataKind.UInt64),
            DataKind.Int64    => ConcatPrimitive<long>(a, b, DataKind.Int64),
            DataKind.Float64  => ConcatPrimitive<double>(a, b, DataKind.Float64),
            DataKind.Time     => ConcatPrimitive<long>(a, b, DataKind.Time),
            DataKind.Duration => ConcatPrimitive<long>(a, b, DataKind.Duration),
            DataKind.Timestamp   => ConcatPrimitive<long>(a, b, DataKind.Timestamp),
            DataKind.TimestampTz => ConcatPrimitive<long>(a, b, DataKind.TimestampTz),
            DataKind.Decimal  => ConcatPrimitive<decimal>(a, b, DataKind.Decimal),
            DataKind.UInt128  => ConcatPrimitive<UInt128>(a, b, DataKind.UInt128),
            DataKind.Int128   => ConcatPrimitive<Int128>(a, b, DataKind.Int128),
            DataKind.Uuid     => ConcatPrimitive<Guid>(a, b, DataKind.Uuid),
            DataKind.Point2D  => ConcatPrimitive<Vector2>(a, b, DataKind.Point2D),

            // Reference-array kinds (carried as ValueRef[] / string[] /
            // byte[] payloads, not as a packed typed array). Not yet
            // wired through this function.
            DataKind.String or DataKind.Struct
                or DataKind.Image or DataKind.Audio
                or DataKind.Video or DataKind.Json
                => throw new FunctionArgumentException(Name,
                    $"array_concat does not yet support Array<{a.ArrayElementKind}>: "
                    + "reference-array kinds use a ValueRef[] payload shape "
                    + "that needs a separate stitching path."),

            // Meta kinds — Unknown is the untyped-NULL sentinel, Type is
            // a type-tag value. Neither has an array form worth concatenating.
            DataKind.Unknown or DataKind.Type
                => throw new FunctionArgumentException(Name,
                    $"array_concat does not support element kind {a.ArrayElementKind}."),

            _ => throw new FunctionArgumentException(Name,
                $"array_concat does not support element kind {a.ArrayElementKind}."),
        });
    }

    /// <summary>
    /// Boolean arrays may be carried as either <c>byte[]</c> (the canonical
    /// payload used by <see cref="ValueRef.FromPrimitiveArray{T}"/>) or
    /// <c>bool[]</c> (also recognised across the ValueRef surface).
    /// Normalise both inputs to <c>byte[]</c> for the output.
    /// </summary>
    private static ValueRef ConcatBoolean(ValueRef a, ValueRef b)
    {
        byte[] left  = AsBooleanBytes(a);
        byte[] right = AsBooleanBytes(b);
        byte[] result = new byte[left.Length + right.Length];
        if (left.Length > 0)
            Array.Copy(left, 0, result, 0, left.Length);
        if (right.Length > 0)
            Array.Copy(right, 0, result, left.Length, right.Length);
        return ValueRef.FromPrimitiveArray(result, DataKind.Boolean);
    }

    private static byte[] AsBooleanBytes(ValueRef arg)
    {
        return arg.Materialized switch
        {
            byte[] bytes => bytes,
            bool[] bools => BoolsToBytes(bools),
            _ => ToTypedArray<byte>(arg),
        };
    }

    private static byte[] BoolsToBytes(bool[] bools)
    {
        byte[] result = new byte[bools.Length];
        for (int i = 0; i < bools.Length; i++)
            result[i] = bools[i] ? (byte)1 : (byte)0;
        return result;
    }

    /// <summary>
    /// Fast path for primitive element kinds whose materialised payload is
    /// a typed <c>T[]</c>. One allocation for the result, two
    /// <see cref="Buffer.BlockCopy"/>s for the data.
    /// </summary>
    private static ValueRef ConcatPrimitive<T>(ValueRef a, ValueRef b, DataKind kind)
        where T : unmanaged
    {
        T[] left  = a.Materialized as T[] ?? ToTypedArray<T>(a);
        T[] right = b.Materialized as T[] ?? ToTypedArray<T>(b);
        T[] result = new T[left.Length + right.Length];
        if (left.Length > 0)
            Array.Copy(left, 0, result, 0, left.Length);
        if (right.Length > 0)
            Array.Copy(right, 0, result, left.Length, right.Length);
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
                _ when typeof(T) == typeof(int)   && elements[i].TryToInt32(out int i32)
                    => (T)(object)i32,
                _ when typeof(T) == typeof(long)  && elements[i].TryToInt64(out long i64)
                    => (T)(object)i64,
                _ => throw new FunctionArgumentException(Name,
                    $"array element [{i}] of kind {elements[i].Kind} "
                    + $"is not coercible to {typeof(T).Name}."),
            };
        }
        return copied;
    }

}
