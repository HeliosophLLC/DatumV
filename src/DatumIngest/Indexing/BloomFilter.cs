using System.Numerics;
using DatumIngest.Model;

namespace DatumIngest.Indexing;

/// <summary>
/// Probabilistic membership filter using double hashing. Tests whether a value
/// was seen during index construction — false positives are possible, but false
/// negatives are not. Used for join-time chunk pruning: if a bloom filter says
/// "definitely not present", the chunk can be skipped entirely.
/// </summary>
/// <remarks>
/// Uses the Kirsch–Mitzenmacker double-hashing technique to generate K hash
/// positions from two base hashes: h(i) = h1 + i * h2. This avoids the cost
/// of K independent hash functions while maintaining the theoretical false
/// positive rate.
/// </remarks>
public sealed class BloomFilter
{
    private readonly byte[] _bits;
    private readonly int _bitCount;
    private readonly int _hashCount;

    /// <summary>Number of bits in the filter.</summary>
    public int BitCount => _bitCount;

    /// <summary>Number of hash functions used for probing.</summary>
    public int HashCount => _hashCount;

    /// <summary>Size of the filter in bytes.</summary>
    public int SizeInBytes => _bits.Length;

    /// <summary>Raw bit array backing the filter.</summary>
    internal byte[] Bits => _bits;

    /// <summary>
    /// Creates a bloom filter sized for the given parameters.
    /// </summary>
    /// <param name="expectedElements">Expected number of distinct elements to insert.</param>
    /// <param name="falsePositiveRate">Target false positive probability (e.g. 0.01 for 1%).</param>
    public BloomFilter(long expectedElements, double falsePositiveRate = 0.01)
    {
        if (expectedElements <= 0)
        {
            expectedElements = 1;
        }

        if (falsePositiveRate <= 0 || falsePositiveRate >= 1)
        {
            falsePositiveRate = 0.01;
        }

        // Optimal bit count: m = -n * ln(p) / (ln(2))^2
        double ln2Squared = Math.Log(2) * Math.Log(2);
        long optimalBits = (long)Math.Ceiling(-expectedElements * Math.Log(falsePositiveRate) / ln2Squared);

        // Clamp to reasonable bounds: at least 64 bits, at most 256 MiB.
        _bitCount = (int)Math.Clamp(optimalBits, 64, 256 * 1024 * 1024 * 8L);
        _bits = new byte[(_bitCount + 7) / 8];

        // Optimal hash count: k = (m / n) * ln(2)
        int optimalHashCount = (int)Math.Round((double)_bitCount / expectedElements * Math.Log(2));
        _hashCount = Math.Clamp(optimalHashCount, 1, 30);
    }

    /// <summary>
    /// Creates a bloom filter from pre-existing serialized state.
    /// </summary>
    /// <param name="bits">The bit array backing the filter.</param>
    /// <param name="bitCount">Number of valid bits in the array.</param>
    /// <param name="hashCount">Number of hash functions.</param>
    internal BloomFilter(byte[] bits, int bitCount, int hashCount)
    {
        _bits = bits;
        _bitCount = bitCount;
        _hashCount = hashCount;
    }

    /// <summary>
    /// Adds a value to the bloom filter.
    /// </summary>
    /// <param name="value">The value to add. Null values are ignored.</param>
    public void Add(DataValue value)
    {
        if (value.IsNull)
        {
            return;
        }

        (uint hash1, uint hash2) = ComputeHashes(value);

        for (int i = 0; i < _hashCount; i++)
        {
            int position = (int)(((long)hash1 + (long)i * hash2) % _bitCount);
            if (position < 0) position += _bitCount;
            _bits[position >> 3] |= (byte)(1 << (position & 7));
        }
    }

    /// <summary>
    /// Tests whether a value may have been added to the filter.
    /// Returns <c>false</c> only when the value is definitely absent.
    /// Returns <c>true</c> when the value is probably present (subject to false positive rate).
    /// </summary>
    /// <param name="value">The value to test.</param>
    /// <returns><c>true</c> if the value may be present; <c>false</c> if definitely absent.</returns>
    public bool MayContain(DataValue value)
    {
        if (value.IsNull)
        {
            return false;
        }

        (uint hash1, uint hash2) = ComputeHashes(value);

        for (int i = 0; i < _hashCount; i++)
        {
            int position = (int)(((long)hash1 + (long)i * hash2) % _bitCount);
            if (position < 0) position += _bitCount;

            if ((_bits[position >> 3] & (1 << (position & 7))) == 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Computes two independent hash values for double hashing using
    /// FNV-1a and a variant with different offset basis.
    /// </summary>
    private static (uint Hash1, uint Hash2) ComputeHashes(DataValue value)
    {
        ReadOnlySpan<byte> bytes = GetValueBytes(value);

        uint hash1 = Fnv1a(bytes);
        uint hash2 = Fnv1aAlternate(bytes);

        // Ensure hash2 is odd so it's coprime with any power-of-two modulus.
        hash2 |= 1;

        return (hash1, hash2);
    }

    /// <summary>
    /// Converts a <see cref="DataValue"/> to a byte representation suitable for hashing.
    /// </summary>
    private static ReadOnlySpan<byte> GetValueBytes(DataValue value)
    {
        switch (value.Kind)
        {
            case DataKind.Float32:
            {
                float scalar = value.AsFloat32();
                return BitConverter.GetBytes(scalar);
            }
            case DataKind.UInt8:
            {
                return new byte[] { value.AsUInt8() };
            }
            case DataKind.String:
            {
                return System.Text.Encoding.UTF8.GetBytes(value.AsString());
            }
            case DataKind.Date:
            {
                return BitConverter.GetBytes(value.AsDate().DayNumber);
            }
            case DataKind.DateTime:
            {
                return BitConverter.GetBytes(value.AsDateTime().ToUnixTimeMilliseconds());
            }
            case DataKind.JsonValue:
            {
                return System.Text.Encoding.UTF8.GetBytes(value.AsJsonValue());
            }
            case DataKind.UInt8Array:
            {
                return value.AsUInt8Array();
            }
            case DataKind.Vector:
            {
                float[] vector = value.AsVector();
                byte[] bytes = new byte[vector.Length * 4];
                Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
                return bytes;
            }
            default:
            {
                // For complex types (Matrix, Tensor, Image), use their hash code.
                return BitConverter.GetBytes(value.GetHashCode());
            }
        }
    }

    /// <summary>FNV-1a 32-bit hash.</summary>
    private static uint Fnv1a(ReadOnlySpan<byte> data)
    {
        uint hash = 2166136261u;

        foreach (byte b in data)
        {
            hash ^= b;
            hash *= 16777619u;
        }

        return hash;
    }

    /// <summary>FNV-1a variant with a different offset basis to produce an independent hash.</summary>
    private static uint Fnv1aAlternate(ReadOnlySpan<byte> data)
    {
        uint hash = 0x811c9dc5u ^ 0x9e3779b9u;

        foreach (byte b in data)
        {
            hash ^= b;
            hash *= 16777619u;
        }

        return hash;
    }
}
