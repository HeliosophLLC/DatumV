using System.Numerics;
using DatumIngest.DatumFile.Compression;

namespace DatumIngest.Indexing.Bitmap;

/// <summary>
/// A fixed-size bitset representing the presence of a single distinct value within a single
/// index chunk. Each bit corresponds to a row offset within the chunk: bit <c>i</c> is set
/// when the value appears at row <c>i</c>.
/// </summary>
/// <remarks>
/// The backing array is <c>ceil(rowCount / 8)</c> bytes. Bits within each byte are indexed
/// from least-significant (bit 0 = row offset <c>byteIndex * 8</c>) to most-significant.
/// Zstd compression is used for on-disk storage; decompression restores the full bitset.
/// </remarks>
internal readonly struct ChunkBitmap
{
    private readonly byte[] _bits;
    private readonly int _rowCount;

    /// <summary>
    /// Creates a chunk bitmap from a pre-populated bit array.
    /// </summary>
    /// <param name="bits">The raw bitset bytes. Length must be <c>ceil(rowCount / 8)</c>.</param>
    /// <param name="rowCount">The number of rows this bitmap covers.</param>
    internal ChunkBitmap(byte[] bits, int rowCount)
    {
        _bits = bits;
        _rowCount = rowCount;
    }

    /// <summary>The number of rows this bitmap covers.</summary>
    internal int RowCount => _rowCount;

    /// <summary>The raw bitset bytes.</summary>
    internal ReadOnlySpan<byte> Bits => _bits;

    /// <summary>The number of bytes in the bitset.</summary>
    internal int ByteLength => _bits.Length;

    /// <summary>Whether no bits are set in this bitmap.</summary>
    internal bool IsEmpty
    {
        get
        {
            for (int i = 0; i < _bits.Length; i++)
            {
                if (_bits[i] != 0)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Creates a new all-zero bitmap for the given row count.
    /// </summary>
    /// <param name="rowCount">Number of rows in the chunk.</param>
    /// <returns>A bitmap with no bits set.</returns>
    internal static ChunkBitmap Create(int rowCount)
    {
        int byteCount = (rowCount + 7) / 8;
        byte[] bits = new byte[byteCount];
        return new ChunkBitmap(bits, rowCount);
    }

    /// <summary>
    /// Decompresses a Zstd-compressed bitset into a <see cref="ChunkBitmap"/>.
    /// </summary>
    /// <param name="compressed">Zstd-compressed bitset bytes.</param>
    /// <param name="rowCount">Number of rows the uncompressed bitmap covers.</param>
    /// <returns>The decompressed chunk bitmap.</returns>
    internal static ChunkBitmap FromCompressed(byte[] compressed, int rowCount)
    {
        int uncompressedLength = (rowCount + 7) / 8;
        byte[] bits = DatumCompressor.Decompress(compressed, uncompressedLength, DatumFile.DatumCompression.Zstd);
        return new ChunkBitmap(bits, rowCount);
    }

    /// <summary>
    /// Compresses the bitset using Zstd.
    /// </summary>
    /// <returns>The Zstd-compressed bitset bytes.</returns>
    internal byte[] Compress()
    {
        return DatumCompressor.Compress(_bits, DatumFile.DatumCompression.Zstd);
    }

    /// <summary>
    /// Returns whether the bit at the given row offset is set.
    /// </summary>
    /// <param name="rowOffset">Zero-based row offset within the chunk.</param>
    /// <returns><c>true</c> if the bit is set.</returns>
    internal bool IsSet(int rowOffset)
    {
        int byteIndex = rowOffset >> 3;
        int bitIndex = rowOffset & 7;
        return (_bits[byteIndex] & (1 << bitIndex)) != 0;
    }

    /// <summary>
    /// Sets the bit at the given row offset.
    /// </summary>
    /// <param name="rowOffset">Zero-based row offset within the chunk.</param>
    internal void SetBit(int rowOffset)
    {
        int byteIndex = rowOffset >> 3;
        int bitIndex = rowOffset & 7;
        _bits[byteIndex] |= (byte)(1 << bitIndex);
    }

    /// <summary>
    /// Clears the bit at the given row offset.
    /// </summary>
    /// <param name="rowOffset">Zero-based row offset within the chunk.</param>
    internal void ClearBit(int rowOffset)
    {
        int byteIndex = rowOffset >> 3;
        int bitIndex = rowOffset & 7;
        _bits[byteIndex] &= (byte)~(1 << bitIndex);
    }

    /// <summary>
    /// Counts the number of set bits in this bitmap.
    /// </summary>
    /// <returns>The population count.</returns>
    internal int PopCount()
    {
        return BitmapComposer.PopCount(_bits);
    }

    /// <summary>
    /// Enumerates the row offsets of all set bits.
    /// </summary>
    /// <returns>An enumerable of zero-based row offsets where the bit is set.</returns>
    internal IEnumerable<int> EnumerateSetBits()
    {
        return BitmapComposer.EnumerateSetBits(_bits, _rowCount);
    }
}
