using System.Numerics;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Arrays;

/// <summary>
/// Sorted (ascending) copy of a flat typed array using the element kind's
/// natural ordering. Supports the same element kinds as
/// <see cref="ArrayMinFunction"/> — numeric, Boolean, temporal, Uuid —
/// plus String (ordinal). Multi-dim arrays are rejected; sorting across a
/// flattened tensor has no meaningful semantic. Null arrays return a
/// typed-null array of the same element kind.
/// </summary>
public sealed class ArraySortFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "array_sort";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Array;

    /// <inheritdoc />
    public static string Description =>
        "Returns an ascending-sorted copy of a flat (1-D) array. Supports "
        + "numeric, Boolean, temporal (Date/Time/Duration/Timestamp/"
        + "TimestampTz), Uuid, and String element kinds. Multi-dim arrays "
        + "are rejected.";

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
        FunctionMetadata.Validate<ArraySortFunction>(argumentKinds);

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
            DataKind.Boolean     => SortBoolean(arrayArg),
            DataKind.UInt8       => SortPrimitive<byte>(arrayArg, DataKind.UInt8),
            DataKind.Int8        => SortPrimitive<sbyte>(arrayArg, DataKind.Int8),
            DataKind.UInt16      => SortPrimitive<ushort>(arrayArg, DataKind.UInt16),
            DataKind.Int16       => SortPrimitive<short>(arrayArg, DataKind.Int16),
            DataKind.Float16     => SortPrimitive<Half>(arrayArg, DataKind.Float16),
            DataKind.UInt32      => SortPrimitive<uint>(arrayArg, DataKind.UInt32),
            DataKind.Int32       => SortPrimitive<int>(arrayArg, DataKind.Int32),
            DataKind.Float32     => SortPrimitive<float>(arrayArg, DataKind.Float32),
            DataKind.Date        => SortPrimitive<int>(arrayArg, DataKind.Date),
            DataKind.UInt64      => SortPrimitive<ulong>(arrayArg, DataKind.UInt64),
            DataKind.Int64       => SortPrimitive<long>(arrayArg, DataKind.Int64),
            DataKind.Float64     => SortPrimitive<double>(arrayArg, DataKind.Float64),
            DataKind.Time        => SortPrimitive<long>(arrayArg, DataKind.Time),
            DataKind.Duration    => SortPrimitive<long>(arrayArg, DataKind.Duration),
            DataKind.Timestamp   => SortPrimitive<long>(arrayArg, DataKind.Timestamp),
            DataKind.TimestampTz => SortPrimitive<long>(arrayArg, DataKind.TimestampTz),
            DataKind.Decimal     => SortPrimitive<decimal>(arrayArg, DataKind.Decimal),
            DataKind.UInt128     => SortPrimitive<UInt128>(arrayArg, DataKind.UInt128),
            DataKind.Int128      => SortPrimitive<Int128>(arrayArg, DataKind.Int128),
            DataKind.Uuid        => SortPrimitive<Guid>(arrayArg, DataKind.Uuid),

            DataKind.String      => SortString(arrayArg),

            // No natural scalar ordering on Point2D / Point3D / Struct /
            // media / Json; reject rather than pick an arbitrary axis.
            DataKind.Point2D or DataKind.Point3D
                or DataKind.Struct
                or DataKind.Image or DataKind.Audio
                or DataKind.Video or DataKind.Json
                => throw new FunctionArgumentException(Name,
                    $"does not support element kind {arrayArg.ArrayElementKind}: no natural ordering."),

            _ => throw new FunctionArgumentException(Name,
                $"does not support element kind {arrayArg.ArrayElementKind}."),
        });
    }

    private static ValueRef SortBoolean(ValueRef arg)
    {
        // Boolean ordering is false < true; counting sort over the byte/bool payload.
        byte[] source = arg.Materialized switch
        {
            byte[] bytes => bytes,
            bool[] bools => BoolsToBytes(bools),
            _ => ToTypedArray<byte>(arg),
        };
        byte[] result = new byte[source.Length];
        int falses = 0;
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == 0) falses++;
        }
        for (int i = falses; i < source.Length; i++)
        {
            result[i] = 1;
        }
        return ValueRef.FromPrimitiveArray(result, DataKind.Boolean);
    }

    private static byte[] BoolsToBytes(bool[] bools)
    {
        byte[] result = new byte[bools.Length];
        for (int i = 0; i < bools.Length; i++)
            result[i] = bools[i] ? (byte)1 : (byte)0;
        return result;
    }

    private static ValueRef SortPrimitive<T>(ValueRef arg, DataKind kind)
        where T : unmanaged, IComparable<T>
    {
        T[] source = arg.Materialized as T[] ?? ToTypedArray<T>(arg);
        T[] result = new T[source.Length];
        Array.Copy(source, result, source.Length);
        Array.Sort(result);
        return ValueRef.FromPrimitiveArray(result, kind);
    }

    private static ValueRef SortString(ValueRef arg)
    {
        string[] source = MaterializeStrings(arg);
        string[] sorted = new string[source.Length];
        Array.Copy(source, sorted, source.Length);
        // Ordinal sort with nulls last — keeps semantics matching the
        // engine's ORDER BY default and avoids an InvalidOperationException
        // when the array carries null slots.
        Array.Sort(sorted, NullsLastOrdinal);

        ValueRef[] refs = new ValueRef[sorted.Length];
        for (int i = 0; i < sorted.Length; i++)
        {
            refs[i] = sorted[i] is null
                ? ValueRef.Null(DataKind.String)
                : ValueRef.FromString(sorted[i]);
        }
        return ValueRef.FromArray(DataKind.String, refs);
    }

    private static int NullsLastOrdinal(string? a, string? b)
    {
        if (a is null && b is null) return 0;
        if (a is null) return 1;
        if (b is null) return -1;
        return string.CompareOrdinal(a, b);
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
