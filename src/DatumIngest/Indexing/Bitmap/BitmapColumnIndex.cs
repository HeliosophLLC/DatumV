using DatumIngest.Model;

namespace DatumIngest.Indexing.Bitmap;

/// <summary>
/// A bitmap index for a single column, storing one compressed bitset per distinct value
/// per chunk. Implements <see cref="IBitmapColumnIndex"/> with on-demand Zstd decompression.
/// </summary>
/// <remarks>
/// The internal structure is a dictionary mapping each distinct <see cref="DataValue"/> to an
/// array of compressed byte arrays, one per chunk. <see cref="GetChunkBitmap"/> decompresses
/// on demand. For a column with cardinality <c>C</c> across <c>K</c> chunks, this stores
/// <c>C × K</c> compressed bitsets, each representing which rows in a given chunk contain
/// the corresponding value.
/// </remarks>
internal sealed class BitmapColumnIndex : IBitmapColumnIndex
{
    private readonly Dictionary<DataValue, byte[][]> _compressedBitmaps;
    private readonly int _chunkCount;
    private readonly int[] _chunkRowCounts;

    /// <summary>
    /// Creates a bitmap column index from pre-compressed per-value, per-chunk bitsets.
    /// </summary>
    /// <param name="compressedBitmaps">
    /// Dictionary mapping each distinct value to an array of Zstd-compressed bitsets,
    /// indexed by chunk index. An empty byte array indicates the value is absent from that chunk.
    /// </param>
    /// <param name="chunkCount">Number of chunks.</param>
    /// <param name="chunkRowCounts">
    /// Array of row counts per chunk (needed for decompression target size).
    /// </param>
    internal BitmapColumnIndex(
        Dictionary<DataValue, byte[][]> compressedBitmaps,
        int chunkCount,
        int[] chunkRowCounts)
    {
        _compressedBitmaps = compressedBitmaps;
        _chunkCount = chunkCount;
        _chunkRowCounts = chunkRowCounts;
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<DataValue> DistinctValues => _compressedBitmaps.Keys;

    /// <inheritdoc/>
    public int ChunkCount => _chunkCount;

    /// <summary>
    /// The row counts per chunk (needed for bitmap decompression and sizing).
    /// </summary>
    internal IReadOnlyList<int> ChunkRowCounts => _chunkRowCounts;

    /// <summary>
    /// The underlying compressed bitmap data for serialization.
    /// </summary>
    internal IReadOnlyDictionary<DataValue, byte[][]> CompressedBitmaps => _compressedBitmaps;

    /// <inheritdoc/>
    public ChunkBitmap GetChunkBitmap(DataValue value, int chunkIndex)
    {
        if (chunkIndex < 0 || chunkIndex >= _chunkCount)
        {
            return ChunkBitmap.Create(0);
        }

        if (!_compressedBitmaps.TryGetValue(value, out byte[][]? chunkBitmaps)
            || chunkIndex >= chunkBitmaps.Length)
        {
            return ChunkBitmap.Create(_chunkRowCounts[chunkIndex]);
        }

        byte[] compressed = chunkBitmaps[chunkIndex];

        if (compressed.Length == 0)
        {
            return ChunkBitmap.Create(_chunkRowCounts[chunkIndex]);
        }

        return ChunkBitmap.FromCompressed(compressed, _chunkRowCounts[chunkIndex]);
    }

    /// <inheritdoc/>
    public bool ChunkContainsValue(DataValue value, int chunkIndex)
    {
        if (!_compressedBitmaps.TryGetValue(value, out byte[][]? chunkBitmaps)
            || chunkIndex < 0
            || chunkIndex >= chunkBitmaps.Length)
        {
            return false;
        }

        // A non-empty compressed payload means at least one bit is set.
        // An all-zero bitmap compresses to a small but non-empty payload,
        // so we decompress and check when the compressed data exists.
        byte[] compressed = chunkBitmaps[chunkIndex];

        if (compressed.Length == 0)
        {
            return false;
        }

        ChunkBitmap bitmap = ChunkBitmap.FromCompressed(compressed, _chunkRowCounts[chunkIndex]);
        return !bitmap.IsEmpty;
    }

    /// <inheritdoc/>
    public IReadOnlySet<int> FindChunksContaining(DataValue value)
    {
        HashSet<int> chunks = new();

        if (!_compressedBitmaps.TryGetValue(value, out byte[][]? chunkBitmaps))
        {
            return chunks;
        }

        for (int chunkIndex = 0; chunkIndex < chunkBitmaps.Length; chunkIndex++)
        {
            if (chunkBitmaps[chunkIndex].Length > 0)
            {
                ChunkBitmap bitmap = ChunkBitmap.FromCompressed(
                    chunkBitmaps[chunkIndex], _chunkRowCounts[chunkIndex]);

                if (!bitmap.IsEmpty)
                {
                    chunks.Add(chunkIndex);
                }
            }
        }

        return chunks;
    }
}
