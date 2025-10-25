using System.Buffers.Binary;
using System.IO.Hashing;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Runtime.InteropServices;
using DatumIngest.Model;

namespace DatumIngest.Indexing.Bloom;

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
    private readonly byte[]? _bits;
    private readonly MemoryMappedViewAccessor? _accessor;
    private readonly long _bitsOffset;
    private readonly int _bitCount;
    private readonly int _hashCount;

    /// <summary>Number of bits in the filter.</summary>
    public int BitCount => _bitCount;

    /// <summary>Number of hash functions used for probing.</summary>
    public int HashCount => _hashCount;

    /// <summary>Size of the filter in bytes.</summary>
    public int SizeInBytes => _bits?.Length ?? ((_bitCount + 7) / 8);

    /// <summary>
    /// Raw bit array backing the filter. For memory-mapped filters, reads the
    /// bytes from the accessor into a new array (serialization path only).
    /// </summary>
    internal byte[] Bits
    {
        get
        {
            if (_bits is not null)
            {
                return _bits;
            }

            byte[] buffer = new byte[(_bitCount + 7) / 8];
            _accessor!.ReadArray(_bitsOffset, buffer.AsSpan());
            return buffer;
        }
    }

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
    /// Creates a memory-mapped bloom filter that reads bits directly from
    /// a <see cref="MemoryMappedViewAccessor"/> without materializing a byte array.
    /// </summary>
    /// <param name="accessor">The shared view accessor spanning the index file.</param>
    /// <param name="bitsOffset">Absolute byte offset of this filter's bit array in the file.</param>
    /// <param name="bitCount">Number of valid bits in the filter.</param>
    /// <param name="hashCount">Number of hash functions.</param>
    internal BloomFilter(MemoryMappedViewAccessor accessor, long bitsOffset, int bitCount, int hashCount)
    {
        _accessor = accessor;
        _bitsOffset = bitsOffset;
        _bitCount = bitCount;
        _hashCount = hashCount;
    }

    /// <summary>
    /// Adds a value to the bloom filter.
    /// </summary>
    /// <param name="value">The value to add. Null values are ignored.</param>
    /// <param name="store">The value store to use for decoding variable-length values.</param>
    public void Add(DataValue value, IValueStore store)
    {
        if (value.IsNull)
        {
            return;
        }

        if (_bits is null)
        {
            throw new InvalidOperationException("Cannot add values to a memory-mapped bloom filter.");
        }

        (uint hash1, uint hash2) = ComputeHashes(value, store);

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
    /// <param name="store">The value store to use for decoding variable-length values.</param>
    /// <returns><c>true</c> if the value may be present; <c>false</c> if definitely absent.</returns>
    public bool MayContain(DataValue value, IValueStore store)
    {
        if (value.IsNull)
        {
            return false;
        }

        (uint hash1, uint hash2) = ComputeHashes(value, store);

        for (int i = 0; i < _hashCount; i++)
        {
            int position = (int)(((long)hash1 + (long)i * hash2) % _bitCount);
            if (position < 0) position += _bitCount;

            byte bitByte;

            if (_bits is not null)
            {
                bitByte = _bits[position >> 3];
            }
            else
            {
                bitByte = _accessor!.ReadByte(_bitsOffset + (position >> 3));
            }

            if ((bitByte & (1 << (position & 7))) == 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Computes two independent hash values for double hashing using
    /// FNV-1a and a variant with different offset basis.
    /// Fixed-size numeric types are hashed directly from a stack-allocated
    /// buffer, avoiding per-call heap allocations.
    /// </summary>
    private static (uint Hash1, uint Hash2) ComputeHashes(DataValue value, IValueStore store)
    {
        // Fast path: fixed-size types hash from stackalloc without any heap allocation.
        switch (value.Kind)
        {
            case DataKind.Boolean:
            {
                Span<byte> buffer = stackalloc byte[1];
                buffer[0] = value.AsBoolean() ? (byte)1 : (byte)0;
                return FinalizeHashes(buffer);
            }
            case DataKind.Int8:
            {
                Span<byte> buffer = stackalloc byte[1];
                buffer[0] = unchecked((byte)value.AsInt8());
                return FinalizeHashes(buffer);
            }
            case DataKind.UInt8:
            {
                Span<byte> buffer = stackalloc byte[1];
                buffer[0] = value.AsUInt8();
                return FinalizeHashes(buffer);
            }
            case DataKind.Int16:
            {
                Span<byte> buffer = stackalloc byte[2];
                BinaryPrimitives.WriteInt16LittleEndian(buffer, value.AsInt16());
                return FinalizeHashes(buffer);
            }
            case DataKind.UInt16:
            {
                Span<byte> buffer = stackalloc byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(buffer, value.AsUInt16());
                return FinalizeHashes(buffer);
            }
            case DataKind.Int32:
            {
                Span<byte> buffer = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(buffer, value.AsInt32());
                return FinalizeHashes(buffer);
            }
            case DataKind.UInt32:
            {
                Span<byte> buffer = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(buffer, value.AsUInt32());
                return FinalizeHashes(buffer);
            }
            case DataKind.Float32:
            {
                Span<byte> buffer = stackalloc byte[4];
                BinaryPrimitives.WriteSingleLittleEndian(buffer, value.AsFloat32());
                return FinalizeHashes(buffer);
            }
            case DataKind.Date:
            {
                Span<byte> buffer = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(buffer, value.AsDate().DayNumber);
                return FinalizeHashes(buffer);
            }
            case DataKind.Int64:
            {
                Span<byte> buffer = stackalloc byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(buffer, value.AsInt64());
                return FinalizeHashes(buffer);
            }
            case DataKind.UInt64:
            {
                Span<byte> buffer = stackalloc byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(buffer, value.AsUInt64());
                return FinalizeHashes(buffer);
            }
            case DataKind.Float64:
            {
                Span<byte> buffer = stackalloc byte[8];
                BinaryPrimitives.WriteDoubleLittleEndian(buffer, value.AsFloat64());
                return FinalizeHashes(buffer);
            }
            case DataKind.DateTime:
            {
                Span<byte> buffer = stackalloc byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(buffer, value.AsDateTime().ToUnixTimeMilliseconds());
                return FinalizeHashes(buffer);
            }
            case DataKind.String:
            case DataKind.JsonValue:
            {
                ulong h = value.RawContentHash;
                if (h == 0) h = XxHash64.HashToUInt64(value.AsUtf8Span(store));
                uint h1 = (uint)h;
                uint h2 = (uint)(h >> 32) | 1;   // preserve the odd trick
                return (h1, h2);
            }
            default:
                return FinalizeHashes(GetValueBytesHeap(value, store));
        }
    }

    /// <summary>
    /// Produces two FNV-1a hashes from a byte span and ensures h2 is odd
    /// for coprimality with power-of-two moduli.
    /// </summary>
    private static (uint Hash1, uint Hash2) FinalizeHashes(ReadOnlySpan<byte> bytes)
    {
        uint hash1 = Fnv1a(bytes);
        uint hash2 = Fnv1aAlternate(bytes);
        hash2 |= 1;
        return (hash1, hash2);
    }

    /// <summary>
    /// Heap-allocating fallback for variable-length and complex types
    /// that cannot use a fixed-size stack buffer.
    /// </summary>
    private static ReadOnlySpan<byte> GetValueBytesHeap(DataValue value, IValueStore store)
    {
        switch (value.Kind)
        {
            case DataKind.UInt8Array:
                return value.AsUInt8Array(store);
            case DataKind.Vector:
                return MemoryMarshal.AsBytes(value.AsVector(store).AsSpan());
            default:
            {
                // Matrix, Tensor, Image, and any future complex types.
                byte[] fallback = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(fallback, value.GetHashCode());
                return fallback;
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
