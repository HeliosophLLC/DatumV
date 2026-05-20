using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Indexing.Bitmap;

/// <summary>
/// Provides chunk-level and row-level bitmap access for a single column's bitmap index.
/// Unlike <see cref="IColumnIndex"/>, bitmap indexes are unordered — they return bitsets
/// (row inclusion masks) rather than sorted entry lists, and do not support ordered traversal.
/// </summary>
/// <remarks>
/// Bitmap indexes are built for low-cardinality columns (≤ 256 distinct values). For each
/// distinct value, a per-chunk bitset records exactly which rows contain that value,
/// enabling efficient row-level filtering and multi-column composition via bitwise AND/OR/NOT.
/// </remarks>
internal interface IBitmapColumnIndex
{
    /// <summary>
    /// The distinct values present in this column's bitmap index.
    /// </summary>
    IReadOnlyCollection<DataValue> DistinctValues { get; }

    /// <summary>
    /// The number of chunks covered by this bitmap index.
    /// </summary>
    int ChunkCount { get; }

    /// <summary>
    /// Retrieves the decompressed bitset for a specific value and chunk.
    /// Each bit in the returned bitmap corresponds to a row offset within the chunk.
    /// </summary>
    /// <param name="value">The distinct value to look up.</param>
    /// <param name="chunkIndex">Zero-based chunk index.</param>
    /// <returns>
    /// The decompressed <see cref="ChunkBitmap"/>, or an empty bitmap if the value does not
    /// appear in the specified chunk.
    /// </returns>
    ChunkBitmap GetChunkBitmap(DataValue value, int chunkIndex);

    /// <summary>
    /// Checks whether a value appears in any row of the specified chunk.
    /// This is a fast non-zero check on the compressed bitset — it does not decompress
    /// the full bitmap when the answer can be determined from the compressed size alone.
    /// </summary>
    /// <param name="value">The value to check for.</param>
    /// <param name="chunkIndex">Zero-based chunk index.</param>
    /// <returns><c>true</c> if at least one row in the chunk contains the value.</returns>
    bool ChunkContainsValue(DataValue value, int chunkIndex);

    /// <summary>
    /// Returns the set of chunk indexes that contain at least one row with the specified value.
    /// </summary>
    /// <param name="value">The value to search for.</param>
    /// <returns>A set of zero-based chunk indexes.</returns>
    IReadOnlySet<int> FindChunksContaining(DataValue value);
}
