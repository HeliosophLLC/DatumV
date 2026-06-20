using System.Numerics;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Arrays;

/// <summary>
/// Reversed copy of a flat typed array. The element kind round-trips —
/// every fixed-width primitive (integers, floats, Decimal, Boolean,
/// temporals, Uuid, Point2D) plus String. Multi-dim arrays are rejected
/// because a per-axis reversal is the only meaningful semantic and that
/// is not what this function provides. Null arrays return a typed-null
/// array of the same element kind.
/// </summary>
public sealed class ArrayReverseFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "array_reverse";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Array;

    /// <inheritdoc />
    public static string Description =>
        "Returns a reversed copy of a flat (1-D) array. Multi-dim arrays "
        + "are rejected. Supports every fixed-width primitive element kind "
        + "plus String.";

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
        FunctionMetadata.Validate<ArrayReverseFunction>(argumentKinds);

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
            DataKind.Boolean     => ReverseBoolean(arrayArg),
            DataKind.UInt8       => ReversePrimitive<byte>(arrayArg, DataKind.UInt8),
            DataKind.Int8        => ReversePrimitive<sbyte>(arrayArg, DataKind.Int8),
            DataKind.UInt16      => ReversePrimitive<ushort>(arrayArg, DataKind.UInt16),
            DataKind.Int16       => ReversePrimitive<short>(arrayArg, DataKind.Int16),
            DataKind.Float16     => ReversePrimitive<Half>(arrayArg, DataKind.Float16),
            DataKind.UInt32      => ReversePrimitive<uint>(arrayArg, DataKind.UInt32),
            DataKind.Int32       => ReversePrimitive<int>(arrayArg, DataKind.Int32),
            DataKind.Float32     => ReversePrimitive<float>(arrayArg, DataKind.Float32),
            DataKind.Date        => ReversePrimitive<int>(arrayArg, DataKind.Date),
            DataKind.UInt64      => ReversePrimitive<ulong>(arrayArg, DataKind.UInt64),
            DataKind.Int64       => ReversePrimitive<long>(arrayArg, DataKind.Int64),
            DataKind.Float64     => ReversePrimitive<double>(arrayArg, DataKind.Float64),
            DataKind.Time        => ReversePrimitive<long>(arrayArg, DataKind.Time),
            DataKind.Duration    => ReversePrimitive<long>(arrayArg, DataKind.Duration),
            DataKind.Timestamp   => ReversePrimitive<long>(arrayArg, DataKind.Timestamp),
            DataKind.TimestampTz => ReversePrimitive<long>(arrayArg, DataKind.TimestampTz),
            DataKind.Decimal     => ReversePrimitive<decimal>(arrayArg, DataKind.Decimal),
            DataKind.UInt128     => ReversePrimitive<UInt128>(arrayArg, DataKind.UInt128),
            DataKind.Int128      => ReversePrimitive<Int128>(arrayArg, DataKind.Int128),
            DataKind.Uuid        => ReversePrimitive<Guid>(arrayArg, DataKind.Uuid),
            DataKind.Point2D     => ReversePrimitive<Vector2>(arrayArg, DataKind.Point2D),

            DataKind.String      => ReverseString(arrayArg),

            DataKind.Struct
                or DataKind.Image or DataKind.Audio
                or DataKind.Video or DataKind.Json
                => throw new FunctionArgumentException(Name,
                    $"does not yet support Array<{arrayArg.ArrayElementKind}>: "
                    + "reference-array kinds use a payload shape that needs a "
                    + "separate reversal path."),

            _ => throw new FunctionArgumentException(Name,
                $"does not support element kind {arrayArg.ArrayElementKind}."),
        });
    }

    private static ValueRef ReverseBoolean(ValueRef arg)
    {
        byte[] source = arg.Materialized switch
        {
            byte[] bytes => bytes,
            bool[] bools => BoolsToBytes(bools),
            _ => ToTypedArray<byte>(arg),
        };
        byte[] result = new byte[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            result[i] = source[source.Length - 1 - i];
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

    private static ValueRef ReversePrimitive<T>(ValueRef arg, DataKind kind)
        where T : unmanaged
    {
        T[] source = arg.Materialized as T[] ?? ToTypedArray<T>(arg);
        T[] result = new T[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            result[i] = source[source.Length - 1 - i];
        }
        return ValueRef.FromPrimitiveArray(result, kind);
    }

    private static ValueRef ReverseString(ValueRef arg)
    {
        ValueRef[] source = arg.Materialized switch
        {
            ValueRef[] refs => refs,
            string[] strings => StringsToRefs(strings),
            _ => throw new FunctionArgumentException(Name,
                $"String array payload is of unsupported runtime type "
                + $"'{arg.Materialized?.GetType().Name ?? "<null>"}'."),
        };
        ValueRef[] result = new ValueRef[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            result[i] = source[source.Length - 1 - i];
        }
        return ValueRef.FromArray(DataKind.String, result);
    }

    private static ValueRef[] StringsToRefs(string[] strings)
    {
        ValueRef[] result = new ValueRef[strings.Length];
        for (int i = 0; i < strings.Length; i++)
        {
            result[i] = strings[i] is null
                ? ValueRef.Null(DataKind.String)
                : ValueRef.FromString(strings[i]);
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
