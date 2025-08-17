using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using DatumIngest.Model;

namespace DatumIngest.Functions.Math;

/// <summary>
/// Deterministic split function: <c>hash_split(key, seed)</c> returns a float in [0, 1)
/// derived from a stable hash of the key and seed. The same (key, seed) pair always
/// produces the same output, enabling reproducible train/val/test splits via
/// <c>WHERE hash_split(id, 42) &lt; 0.8</c>.
/// </summary>
/// <remarks>
/// Uses XxHash64 with the seed as the hash seed. The 64-bit hash is mapped to [0, 1)
/// by taking the upper 53 bits and dividing by 2^53, giving uniform distribution with
/// double-precision granularity.
/// </remarks>
public sealed class HashSplitFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "hash_split";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
            throw new ArgumentException("hash_split() requires exactly 2 arguments (key, seed).");

        if (argumentKinds[1] is not (DataKind.Float32 or DataKind.UInt8))
            throw new ArgumentException($"hash_split() seed (argument 2) must be Scalar or UInt8, got {argumentKinds[1]}.");

        return DataKind.Float32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue key = arguments[0];
        if (key.IsNull)
            return DataValue.Null(DataKind.Float32);

        long seed = (long)(arguments[1].Kind is DataKind.UInt8
            ? arguments[1].AsUInt8()
            : arguments[1].AsFloat32());

        string keyString = key.ToString()!;
        byte[] keyBytes = Encoding.UTF8.GetBytes(keyString);
        ulong hash = XxHash64.HashToUInt64(keyBytes, seed);

        // Map upper 53 bits to [0, 1) with double-precision granularity.
        double value = (hash >>> 11) * (1.0 / (1UL << 53));
        return DataValue.FromFloat32((float)value);
    }
}

/// <summary>
/// Returns a random integer in [min, max]: <c>random_int(min, max)</c>.
/// Both bounds are inclusive. Uses a thread-safe random number generator.
/// </summary>
public sealed class RandomIntFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "random_int";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
            throw new ArgumentException("random_int() requires exactly 2 arguments (min, max).");

        if (argumentKinds[0] is not (DataKind.Float32 or DataKind.UInt8))
            throw new ArgumentException($"random_int() min (argument 1) must be Scalar or UInt8, got {argumentKinds[0]}.");

        if (argumentKinds[1] is not (DataKind.Float32 or DataKind.UInt8))
            throw new ArgumentException($"random_int() max (argument 2) must be Scalar or UInt8, got {argumentKinds[1]}.");

        return DataKind.Float32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        int min = (int)(arguments[0].Kind is DataKind.UInt8
            ? arguments[0].AsUInt8()
            : arguments[0].AsFloat32());

        int max = (int)(arguments[1].Kind is DataKind.UInt8
            ? arguments[1].AsUInt8()
            : arguments[1].AsFloat32());

        if (min > max)
            throw new ArgumentException($"random_int() min ({min}) must be <= max ({max}).");

        return DataValue.FromFloat32(Random.Shared.Next(min, max + 1));
    }
}

/// <summary>
/// Returns a random float in [min, max): <c>random_range(min, max)</c>.
/// Uses a thread-safe random number generator.
/// </summary>
public sealed class RandomRangeFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "random_range";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
            throw new ArgumentException("random_range() requires exactly 2 arguments (min, max).");

        if (argumentKinds[0] is not (DataKind.Float32 or DataKind.UInt8))
            throw new ArgumentException($"random_range() min (argument 1) must be Scalar or UInt8, got {argumentKinds[0]}.");

        if (argumentKinds[1] is not (DataKind.Float32 or DataKind.UInt8))
            throw new ArgumentException($"random_range() max (argument 2) must be Scalar or UInt8, got {argumentKinds[1]}.");

        return DataKind.Float32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        float min = arguments[0].Kind is DataKind.UInt8
            ? arguments[0].AsUInt8()
            : arguments[0].AsFloat32();

        float max = arguments[1].Kind is DataKind.UInt8
            ? arguments[1].AsUInt8()
            : arguments[1].AsFloat32();

        if (min >= max)
            throw new ArgumentException($"random_range() min ({min}) must be < max ({max}).");

        double value = Random.Shared.NextDouble() * (max - min) + min;
        return DataValue.FromFloat32((float)value);
    }
}

/// <summary>
/// Samples from a normal (Gaussian) distribution: <c>random_normal(mean, stddev)</c>.
/// Uses the Box-Muller transform to convert uniform samples to normal samples.
/// </summary>
public sealed class RandomNormalFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "random_normal";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
            throw new ArgumentException("random_normal() requires exactly 2 arguments (mean, stddev).");

        if (argumentKinds[0] is not (DataKind.Float32 or DataKind.UInt8))
            throw new ArgumentException($"random_normal() mean (argument 1) must be Scalar or UInt8, got {argumentKinds[0]}.");

        if (argumentKinds[1] is not (DataKind.Float32 or DataKind.UInt8))
            throw new ArgumentException($"random_normal() stddev (argument 2) must be Scalar or UInt8, got {argumentKinds[1]}.");

        return DataKind.Float32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        float mean = arguments[0].Kind is DataKind.UInt8
            ? arguments[0].AsUInt8()
            : arguments[0].AsFloat32();

        float stddev = arguments[1].Kind is DataKind.UInt8
            ? arguments[1].AsUInt8()
            : arguments[1].AsFloat32();

        if (stddev < 0)
            throw new ArgumentException($"random_normal() stddev must be non-negative, got {stddev}.");

        float sample = SampleStandardNormal();
        return DataValue.FromFloat32(mean + stddev * sample);
    }

    /// <summary>
    /// Generates a standard normal sample N(0,1) using the Box-Muller transform.
    /// </summary>
    internal static float SampleStandardNormal()
    {
        double u1 = 1.0 - Random.Shared.NextDouble();
        double u2 = Random.Shared.NextDouble();
        return (float)(System.Math.Sqrt(-2.0 * System.Math.Log(u1)) * System.Math.Cos(2.0 * System.Math.PI * u2));
    }
}

/// <summary>
/// Bernoulli trial: <c>random_boolean(probability)</c> returns true with the given
/// probability (0 to 1). Useful for dropout masks, random augmentation selection,
/// and synthetic Boolean column generation.
/// </summary>
public sealed class RandomBooleanFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "random_boolean";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
            throw new ArgumentException("random_boolean() requires exactly 1 argument (probability).");

        if (argumentKinds[0] is not (DataKind.Float32 or DataKind.UInt8))
            throw new ArgumentException($"random_boolean() probability must be Scalar or UInt8, got {argumentKinds[0]}.");

        return DataKind.Boolean;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        float probability = arguments[0].Kind is DataKind.UInt8
            ? arguments[0].AsUInt8()
            : arguments[0].AsFloat32();

        if (probability < 0f || probability > 1f)
            throw new ArgumentException($"random_boolean() probability must be in [0, 1], got {probability}.");

        return DataValue.FromBoolean(Random.Shared.NextDouble() < probability);
    }
}
