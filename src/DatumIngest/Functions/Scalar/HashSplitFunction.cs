using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar;

/// <summary>
/// Returns a deterministic <see cref="DataKind.Float32"/> in the half-open
/// range <c>[0, 1)</c> derived from the key and seed using XxHash64.
/// </summary>
/// <remarks>
/// <para>
/// The same (key, seed) pair always produces the same value, making this
/// suitable for reproducible train/val/test splits:
/// <code>WHERE hash_split(id, 42) &lt; 0.8</code>
/// </para>
/// <para>
/// The key is hashed as its raw byte representation (UTF-8 for strings,
/// little-endian binary for numeric kinds, raw 16 bytes for UUIDs).
/// Null key or null seed yields a null result.
/// </para>
/// </remarks>
public sealed class HashSplitFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "hash_split";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Returns a deterministic Float32 in [0, 1) derived from the key and seed using XxHash64. " +
        "Enables reproducible train/val/test splits.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("key", DataKindMatcher.Any),
                new ParameterSpec("seed", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<HashSplitFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef keyArg = args[0];
        ValueRef seedArg = args[1];

        if (keyArg.IsNull || seedArg.IsNull)
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32));

        long seed = ReadInteger(seedArg);
        ulong hash = HashKey(keyArg, seed);

        // Build a Float32 in [1, 2) from the upper 23 mantissa bits, then subtract 1
        // to get [0, 1). Avoids rounding artefacts that can push naive division to 1.0.
        uint mantissaBits = (uint)(hash >> 41) & 0x007FFFFFu;
        float result = BitConverter.Int32BitsToSingle((int)(0x3F800000u | mantissaBits)) - 1.0f;

        return new ValueTask<ValueRef>(ValueRef.FromFloat32(result));
    }

    private static ulong HashKey(ValueRef key, long seed)
    {
        switch (key.Kind)
        {
            case DataKind.Int8:
            {
                Span<sbyte> buf = stackalloc sbyte[1] { key.AsInt8() };
                return XxHash64.HashToUInt64(MemoryMarshal.AsBytes((ReadOnlySpan<sbyte>)buf), seed);
            }
            case DataKind.UInt8:
            {
                Span<byte> buf = stackalloc byte[1] { key.AsUInt8() };
                return XxHash64.HashToUInt64(buf, seed);
            }
            case DataKind.Int16:
            {
                Span<short> buf = stackalloc short[1] { key.AsInt16() };
                return XxHash64.HashToUInt64(MemoryMarshal.AsBytes((ReadOnlySpan<short>)buf), seed);
            }
            case DataKind.UInt16:
            {
                Span<ushort> buf = stackalloc ushort[1] { key.AsUInt16() };
                return XxHash64.HashToUInt64(MemoryMarshal.AsBytes((ReadOnlySpan<ushort>)buf), seed);
            }
            case DataKind.Int32:
            {
                Span<int> buf = stackalloc int[1] { key.AsInt32() };
                return XxHash64.HashToUInt64(MemoryMarshal.AsBytes((ReadOnlySpan<int>)buf), seed);
            }
            case DataKind.UInt32:
            {
                Span<uint> buf = stackalloc uint[1] { key.AsUInt32() };
                return XxHash64.HashToUInt64(MemoryMarshal.AsBytes((ReadOnlySpan<uint>)buf), seed);
            }
            case DataKind.Int64:
            {
                Span<long> buf = stackalloc long[1] { key.AsInt64() };
                return XxHash64.HashToUInt64(MemoryMarshal.AsBytes((ReadOnlySpan<long>)buf), seed);
            }
            case DataKind.UInt64:
            {
                Span<ulong> buf = stackalloc ulong[1] { key.AsUInt64() };
                return XxHash64.HashToUInt64(MemoryMarshal.AsBytes((ReadOnlySpan<ulong>)buf), seed);
            }
            case DataKind.Float32:
            {
                Span<float> buf = stackalloc float[1] { key.AsFloat32() };
                return XxHash64.HashToUInt64(MemoryMarshal.AsBytes((ReadOnlySpan<float>)buf), seed);
            }
            case DataKind.Float64:
            {
                Span<double> buf = stackalloc double[1] { key.AsFloat64() };
                return XxHash64.HashToUInt64(MemoryMarshal.AsBytes((ReadOnlySpan<double>)buf), seed);
            }
            case DataKind.Uuid:
            {
                Guid guid = key.AsUuid();
                Span<byte> buf = stackalloc byte[16];
                guid.TryWriteBytes(buf);
                return XxHash64.HashToUInt64(buf, seed);
            }
            case DataKind.String:
            {
                string s = key.AsString();
                int byteCount = System.Text.Encoding.UTF8.GetByteCount(s);
                Span<byte> bytes = byteCount <= 256
                    ? stackalloc byte[byteCount]
                    : new byte[byteCount];
                System.Text.Encoding.UTF8.GetBytes(s, bytes);
                return XxHash64.HashToUInt64(bytes, seed);
            }
            default:
                throw new FunctionArgumentException(Name, $"unsupported key kind {key.Kind}.");
        }
    }

    private static long ReadInteger(ValueRef v)
    {
        v.TryToDouble(out double d);
        return (long)d;
    }
}
