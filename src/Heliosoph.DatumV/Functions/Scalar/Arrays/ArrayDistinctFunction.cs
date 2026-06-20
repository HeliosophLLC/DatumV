using System.Numerics;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Arrays;

/// <summary>
/// Returns a copy of a flat typed array with duplicate elements removed,
/// preserving first-occurrence order. Element equality is the natural
/// <see cref="IEquatable{T}"/> on the typed primitive carrier, or ordinal
/// equality for String. Multi-dim arrays are rejected — distinct across a
/// flattened tensor would silently discard shape information. Null arrays
/// return a typed-null array of the same element kind.
/// </summary>
public sealed class ArrayDistinctFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "array_distinct";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Array;

    /// <inheritdoc />
    public static string Description =>
        "Returns a copy of a flat (1-D) array with duplicate elements "
        + "removed, preserving first-occurrence order. Supports every "
        + "fixed-width primitive element kind plus String. Multi-dim "
        + "arrays are rejected.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("array", DataKindMatcher.Any, IsArray: ArrayMatch.FlatArray),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ArrayDistinctFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arrayArg = arguments.Span[0];
        if (arrayArg.IsNull)
        {
            DataKind elementKind = arrayArg.IsArray ? arrayArg.ArrayElementKind : arrayArg.Kind;
            return new ValueTask<ValueRef>(ValueRef.NullArray(elementKind));
        }

        return new ValueTask<ValueRef>(arrayArg.ArrayElementKind switch
        {
            DataKind.Boolean     => DistinctBoolean(arrayArg),
            DataKind.UInt8       => DistinctPrimitive<byte>(arrayArg, DataKind.UInt8),
            DataKind.Int8        => DistinctPrimitive<sbyte>(arrayArg, DataKind.Int8),
            DataKind.UInt16      => DistinctPrimitive<ushort>(arrayArg, DataKind.UInt16),
            DataKind.Int16       => DistinctPrimitive<short>(arrayArg, DataKind.Int16),
            DataKind.Float16     => DistinctPrimitive<Half>(arrayArg, DataKind.Float16),
            DataKind.UInt32      => DistinctPrimitive<uint>(arrayArg, DataKind.UInt32),
            DataKind.Int32       => DistinctPrimitive<int>(arrayArg, DataKind.Int32),
            DataKind.Float32     => DistinctPrimitive<float>(arrayArg, DataKind.Float32),
            DataKind.Date        => DistinctPrimitive<int>(arrayArg, DataKind.Date),
            DataKind.UInt64      => DistinctPrimitive<ulong>(arrayArg, DataKind.UInt64),
            DataKind.Int64       => DistinctPrimitive<long>(arrayArg, DataKind.Int64),
            DataKind.Float64     => DistinctPrimitive<double>(arrayArg, DataKind.Float64),
            DataKind.Time        => DistinctPrimitive<long>(arrayArg, DataKind.Time),
            DataKind.Duration    => DistinctPrimitive<long>(arrayArg, DataKind.Duration),
            DataKind.Timestamp   => DistinctPrimitive<long>(arrayArg, DataKind.Timestamp),
            DataKind.TimestampTz => DistinctPrimitive<long>(arrayArg, DataKind.TimestampTz),
            DataKind.Decimal     => DistinctPrimitive<decimal>(arrayArg, DataKind.Decimal),
            DataKind.UInt128     => DistinctPrimitive<UInt128>(arrayArg, DataKind.UInt128),
            DataKind.Int128      => DistinctPrimitive<Int128>(arrayArg, DataKind.Int128),
            DataKind.Uuid        => DistinctPrimitive<Guid>(arrayArg, DataKind.Uuid),
            DataKind.Point2D     => DistinctPrimitive<Vector2>(arrayArg, DataKind.Point2D),

            DataKind.String      => DistinctString(arrayArg),

            DataKind.Struct
                or DataKind.Image or DataKind.Audio
                or DataKind.Video or DataKind.Json
                => throw new FunctionArgumentException(Name,
                    $"does not yet support Array<{arrayArg.ArrayElementKind}>: "
                    + "reference-array kinds use a payload shape that needs a "
                    + "separate deduplication path."),

            _ => throw new FunctionArgumentException(Name,
                $"does not support element kind {arrayArg.ArrayElementKind}."),
        });
    }

    private static ValueRef DistinctBoolean(ValueRef arg)
    {
        byte[] source = arg.Materialized switch
        {
            byte[] bytes => bytes,
            bool[] bools => BoolsToBytes(bools),
            _ => ToTypedArray<byte>(arg),
        };
        // Bool dedup is at most {false, true} in first-seen order.
        bool sawFalse = false, sawTrue = false;
        List<byte> result = new(2);
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == 0 && !sawFalse) { result.Add(0); sawFalse = true; }
            else if (source[i] != 0 && !sawTrue) { result.Add(1); sawTrue = true; }
            if (sawFalse && sawTrue) break;
        }
        return ValueRef.FromPrimitiveArray(result.ToArray(), DataKind.Boolean);
    }

    private static byte[] BoolsToBytes(bool[] bools)
    {
        byte[] result = new byte[bools.Length];
        for (int i = 0; i < bools.Length; i++)
            result[i] = bools[i] ? (byte)1 : (byte)0;
        return result;
    }

    private static ValueRef DistinctPrimitive<T>(ValueRef arg, DataKind kind)
        where T : unmanaged, IEquatable<T>
    {
        T[] source = arg.Materialized as T[] ?? ToTypedArray<T>(arg);
        HashSet<T> seen = new(source.Length);
        List<T> result = new(source.Length);
        for (int i = 0; i < source.Length; i++)
        {
            if (seen.Add(source[i]))
            {
                result.Add(source[i]);
            }
        }
        return ValueRef.FromPrimitiveArray(result.ToArray(), kind);
    }

    private static ValueRef DistinctString(ValueRef arg)
    {
        string[] source = MaterializeStrings(arg);
        HashSet<string?> seen = new(source.Length, StringComparer.Ordinal);
        List<string?> result = new(source.Length);
        for (int i = 0; i < source.Length; i++)
        {
            if (seen.Add(source[i]))
            {
                result.Add(source[i]);
            }
        }

        ValueRef[] refs = new ValueRef[result.Count];
        for (int i = 0; i < result.Count; i++)
        {
            refs[i] = result[i] is null
                ? ValueRef.Null(DataKind.String)
                : ValueRef.FromString(result[i]!);
        }
        return ValueRef.FromArray(DataKind.String, refs);
    }

    private static string[] MaterializeStrings(ValueRef arg)
    {
        return arg.Materialized switch
        {
            string[] strings => strings,
            ValueRef[] refs => RefsToStrings(refs),
            _ => throw new FunctionArgumentException(Name,
                $"String array payload is of unsupported runtime type "
                + $"'{arg.Materialized?.GetType().Name ?? "<null>"}'."),
        };
    }

    private static string[] RefsToStrings(ValueRef[] refs)
    {
        string[] result = new string[refs.Length];
        for (int i = 0; i < refs.Length; i++)
        {
            result[i] = refs[i].IsNull ? null! : refs[i].AsString();
        }
        return result;
    }

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
