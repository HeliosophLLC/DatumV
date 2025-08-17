using DatumIngest.Model;

namespace DatumIngest.Indexing.Bitmap;

/// <summary>
/// Per-column, per-chunk accumulator that tracks which rows contain each distinct value.
/// Accumulates <see cref="ChunkBitmap"/> instances during row scanning and compresses them
/// at chunk boundaries. Abandons tracking when the distinct value count exceeds the
/// configured cardinality threshold to bound memory usage.
/// </summary>
internal sealed class BitmapChunkAccumulator
{
    private readonly int _cardinalityThreshold;
    private Dictionary<DataValue, ChunkBitmap>? _currentChunkBitmaps;
    private int _currentChunkRowCount;
    private bool _abandoned;

    /// <summary>
    /// Compressed bitmaps accumulated across all finalized chunks.
    /// Outer key: distinct value. Inner list: one compressed byte array per chunk
    /// (indexed by chunk index). Empty byte array means the value was absent from that chunk.
    /// </summary>
    private readonly Dictionary<DataValue, List<byte[]>> _allChunkCompressed = new();

    /// <summary>Row counts for each finalized chunk (needed for decompression sizing).</summary>
    private readonly List<int> _chunkRowCounts = new();

    /// <summary>
    /// Creates a new accumulator with the specified cardinality ceiling.
    /// </summary>
    /// <param name="cardinalityThreshold">
    /// Maximum number of distinct values before the accumulator abandons tracking.
    /// Defaults to <see cref="IndexConstants.BitmapAutoThreshold"/>.
    /// </param>
    internal BitmapChunkAccumulator(int cardinalityThreshold = IndexConstants.BitmapAutoThreshold)
    {
        _cardinalityThreshold = cardinalityThreshold;
    }

    /// <summary>
    /// Whether the accumulator has abandoned tracking because the cardinality exceeded
    /// the threshold. Once abandoned, all subsequent <see cref="Add"/> calls are no-ops.
    /// </summary>
    internal bool IsAbandoned => _abandoned;

    /// <summary>
    /// Initializes the accumulator for a new chunk with the given row capacity.
    /// Must be called before the first <see cref="Add"/> of each chunk.
    /// </summary>
    /// <param name="chunkRowCapacity">
    /// Maximum number of rows expected in this chunk (used to size the bitsets).
    /// </param>
    internal void BeginChunk(int chunkRowCapacity)
    {
        if (_abandoned)
        {
            return;
        }

        _currentChunkBitmaps = new Dictionary<DataValue, ChunkBitmap>();
        _currentChunkRowCount = chunkRowCapacity;
    }

    /// <summary>
    /// Records a value at the specified row offset within the current chunk.
    /// </summary>
    /// <param name="value">The data value to record.</param>
    /// <param name="rowOffsetInChunk">Zero-based row offset within the current chunk.</param>
    internal void Add(DataValue value, int rowOffsetInChunk)
    {
        if (_abandoned || _currentChunkBitmaps is null)
        {
            return;
        }

        if (value.IsNull)
        {
            return;
        }

        if (!_currentChunkBitmaps.TryGetValue(value, out ChunkBitmap bitmap))
        {
            if (_currentChunkBitmaps.Count + CountValuesOnlyInPriorChunks(value) >= _cardinalityThreshold)
            {
                Abandon();
                return;
            }

            bitmap = ChunkBitmap.Create(_currentChunkRowCount);
            _currentChunkBitmaps[value] = bitmap;
        }

        bitmap.SetBit(rowOffsetInChunk);
    }

    /// <summary>
    /// Finalizes the current chunk: compresses all accumulated bitmaps and stores them.
    /// Must be called at each chunk boundary (and after the last row of the final chunk).
    /// </summary>
    /// <param name="actualRowCount">
    /// The actual number of rows in this chunk (may be less than the capacity
    /// passed to <see cref="BeginChunk"/> for the last partial chunk).
    /// </param>
    internal void FinalizeChunk(int actualRowCount)
    {
        if (_abandoned || _currentChunkBitmaps is null)
        {
            return;
        }

        int chunkIndex = _chunkRowCounts.Count;
        _chunkRowCounts.Add(actualRowCount);

        // Compress current chunk bitmaps and store them.
        foreach (KeyValuePair<DataValue, ChunkBitmap> entry in _currentChunkBitmaps)
        {
            if (!_allChunkCompressed.TryGetValue(entry.Key, out List<byte[]>? chunkList))
            {
                chunkList = new List<byte[]>();

                // Backfill empty entries for prior chunks where this value was absent.
                for (int i = 0; i < chunkIndex; i++)
                {
                    chunkList.Add(Array.Empty<byte>());
                }

                _allChunkCompressed[entry.Key] = chunkList;
            }

            // Trim bitmap to actual row count when the chunk is smaller than
            // capacity (last partial chunk) so the compressed size matches
            // the row-count used during decompression.
            ChunkBitmap bitmap = entry.Value;

            if (actualRowCount < _currentChunkRowCount)
            {
                int trimmedByteCount = (actualRowCount + 7) / 8;
                byte[] trimmedBits = new byte[trimmedByteCount];
                bitmap.Bits[..trimmedByteCount].CopyTo(trimmedBits);
                bitmap = new ChunkBitmap(trimmedBits, actualRowCount);
            }

            chunkList.Add(bitmap.Compress());
        }

        // Fill empty bytes for values seen in previous chunks but not in this one.
        foreach (KeyValuePair<DataValue, List<byte[]>> entry in _allChunkCompressed)
        {
            if (entry.Value.Count <= chunkIndex)
            {
                entry.Value.Add(Array.Empty<byte>());
            }
        }

        _currentChunkBitmaps = null;
    }

    /// <summary>
    /// Builds the final <see cref="BitmapColumnIndex"/> from all accumulated chunks.
    /// Returns <c>null</c> if the accumulator was abandoned or no data was collected.
    /// </summary>
    internal BitmapColumnIndex? Build()
    {
        if (_abandoned || _allChunkCompressed.Count == 0)
        {
            return null;
        }

        int chunkCount = _chunkRowCounts.Count;
        Dictionary<DataValue, byte[][]> compressedBitmaps = new(_allChunkCompressed.Count);

        foreach (KeyValuePair<DataValue, List<byte[]>> entry in _allChunkCompressed)
        {
            compressedBitmaps[entry.Key] = entry.Value.ToArray();
        }

        return new BitmapColumnIndex(compressedBitmaps, chunkCount, _chunkRowCounts.ToArray());
    }

    /// <summary>
    /// Checks whether a value exists only in prior chunks (not in the current chunk).
    /// Used for global cardinality tracking: the total distinct count is the union
    /// of all values seen in prior chunks plus new values in the current chunk.
    /// </summary>
    private int CountValuesOnlyInPriorChunks(DataValue value)
    {
        // The total global distinct count is:
        //   values in _allChunkCompressed (seen in prior chunks) that are NOT in _currentChunkBitmaps
        //   + values in _currentChunkBitmaps (seen in current chunk)
        // We only need the global count, which is the union.
        // _currentChunkBitmaps.Count already covers current-chunk values.
        // _allChunkCompressed has prior-chunk values (some may also be in current chunk).
        // Global distinct = _currentChunkBitmaps.Count + (values in _allChunkCompressed NOT in _currentChunkBitmaps).
        // But we're checking if adding ONE more new value exceeds threshold.
        // Total after adding = current count + prior-only + 1.

        int priorOnlyCount = 0;

        foreach (DataValue priorValue in _allChunkCompressed.Keys)
        {
            if (!_currentChunkBitmaps!.ContainsKey(priorValue))
            {
                priorOnlyCount++;
            }
        }

        return priorOnlyCount;
    }

    private void Abandon()
    {
        _abandoned = true;
        _currentChunkBitmaps = null;
        _allChunkCompressed.Clear();
        _chunkRowCounts.Clear();
    }
}
