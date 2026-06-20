using System.Numerics;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions.Scalar.Distributions;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Arrays;

/// <summary>
/// Returns a shuffled copy of a flat typed array using the Fisher-Yates
/// algorithm. The element kind round-trips — every fixed-width primitive
/// (integers, floats, Decimal, Boolean, temporals, Uuid, Point2D) plus
/// String. Multi-dim arrays are rejected; a meaningful shuffle would need
/// to choose an axis. A null array returns a typed-null array of the same
/// element kind. An optional integer seed makes the shuffle deterministic.
/// </summary>
/// <remarks>
/// <see cref="IsPure"/> is <see langword="false"/>: even seeded calls
/// re-evaluate per reference rather than being collapsed by CSE.
/// </remarks>
public sealed class ArrayShuffleFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "array_shuffle";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Array;

    /// <inheritdoc />
    public static string Description =>
        "Returns a shuffled copy of a flat (1-D) array via Fisher-Yates. Accepts an optional "
        + "integer seed for determinism. Multi-dim arrays are rejected.";

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
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("array", DataKindMatcher.Any, IsArray: ArrayMatch.FlatArray),
                new ParameterSpec("seed", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public bool IsPure => false;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ArrayShuffleFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef arrayArg = args[0];

        if (!RandomDistributionsCore.TryGetRng(args, seedIndex: 1, out Random rng))
        {
            DataKind elementKind = arrayArg.IsArray ? arrayArg.ArrayElementKind : arrayArg.Kind;
            return new ValueTask<ValueRef>(ValueRef.NullArray(elementKind));
        }

        if (arrayArg.IsNull)
        {
            DataKind elementKind = arrayArg.IsArray ? arrayArg.ArrayElementKind : arrayArg.Kind;
            return new ValueTask<ValueRef>(ValueRef.NullArray(elementKind));
        }

        return new ValueTask<ValueRef>(arrayArg.ArrayElementKind switch
        {
            DataKind.Boolean     => ShuffleBoolean(arrayArg, rng),
            DataKind.UInt8       => ShufflePrimitive<byte>(arrayArg, DataKind.UInt8, rng),
            DataKind.Int8        => ShufflePrimitive<sbyte>(arrayArg, DataKind.Int8, rng),
            DataKind.UInt16      => ShufflePrimitive<ushort>(arrayArg, DataKind.UInt16, rng),
            DataKind.Int16       => ShufflePrimitive<short>(arrayArg, DataKind.Int16, rng),
            DataKind.Float16     => ShufflePrimitive<Half>(arrayArg, DataKind.Float16, rng),
            DataKind.UInt32      => ShufflePrimitive<uint>(arrayArg, DataKind.UInt32, rng),
            DataKind.Int32       => ShufflePrimitive<int>(arrayArg, DataKind.Int32, rng),
            DataKind.Float32     => ShufflePrimitive<float>(arrayArg, DataKind.Float32, rng),
            DataKind.Date        => ShufflePrimitive<int>(arrayArg, DataKind.Date, rng),
            DataKind.UInt64      => ShufflePrimitive<ulong>(arrayArg, DataKind.UInt64, rng),
            DataKind.Int64       => ShufflePrimitive<long>(arrayArg, DataKind.Int64, rng),
            DataKind.Float64     => ShufflePrimitive<double>(arrayArg, DataKind.Float64, rng),
            DataKind.Time        => ShufflePrimitive<long>(arrayArg, DataKind.Time, rng),
            DataKind.Duration    => ShufflePrimitive<long>(arrayArg, DataKind.Duration, rng),
            DataKind.Timestamp   => ShufflePrimitive<long>(arrayArg, DataKind.Timestamp, rng),
            DataKind.TimestampTz => ShufflePrimitive<long>(arrayArg, DataKind.TimestampTz, rng),
            DataKind.Decimal     => ShufflePrimitive<decimal>(arrayArg, DataKind.Decimal, rng),
            DataKind.UInt128     => ShufflePrimitive<UInt128>(arrayArg, DataKind.UInt128, rng),
            DataKind.Int128      => ShufflePrimitive<Int128>(arrayArg, DataKind.Int128, rng),
            DataKind.Uuid        => ShufflePrimitive<Guid>(arrayArg, DataKind.Uuid, rng),
            DataKind.Point2D     => ShufflePrimitive<Vector2>(arrayArg, DataKind.Point2D, rng),

            DataKind.String      => ShuffleString(arrayArg, rng),

            DataKind.Struct
                or DataKind.Image or DataKind.Audio
                or DataKind.Video or DataKind.Json
                => throw new FunctionArgumentException(Name,
                    $"does not yet support Array<{arrayArg.ArrayElementKind}>: "
                    + "reference-array kinds use a payload shape that needs a "
                    + "separate shuffle path."),

            _ => throw new FunctionArgumentException(Name,
                $"does not support element kind {arrayArg.ArrayElementKind}."),
        });
    }

    private static ValueRef ShuffleBoolean(ValueRef arg, Random rng)
    {
        byte[] source = arg.Materialized switch
        {
            byte[] bytes => bytes,
            bool[] bools => BoolsToBytes(bools),
            _ => ToTypedArray<byte>(arg),
        };
        byte[] result = (byte[])source.Clone();
        FisherYates(result, rng);
        return ValueRef.FromPrimitiveArray(result, DataKind.Boolean);
    }

    private static byte[] BoolsToBytes(bool[] bools)
    {
        byte[] result = new byte[bools.Length];
        for (int i = 0; i < bools.Length; i++)
            result[i] = bools[i] ? (byte)1 : (byte)0;
        return result;
    }

    private static ValueRef ShufflePrimitive<T>(ValueRef arg, DataKind kind, Random rng)
        where T : unmanaged
    {
        T[] source = arg.Materialized as T[] ?? ToTypedArray<T>(arg);
        T[] result = (T[])source.Clone();
        FisherYates(result, rng);
        return ValueRef.FromPrimitiveArray(result, kind);
    }

    private static ValueRef ShuffleString(ValueRef arg, Random rng)
    {
        ValueRef[] source = arg.Materialized switch
        {
            ValueRef[] refs => refs,
            string[] strings => StringsToRefs(strings),
            _ => throw new FunctionArgumentException(Name,
                $"String array payload is of unsupported runtime type "
                + $"'{arg.Materialized?.GetType().Name ?? "<null>"}'."),
        };
        ValueRef[] result = (ValueRef[])source.Clone();
        FisherYates(result, rng);
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

    private static void FisherYates<T>(T[] buffer, Random rng)
    {
        for (int i = buffer.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (buffer[i], buffer[j]) = (buffer[j], buffer[i]);
        }
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
