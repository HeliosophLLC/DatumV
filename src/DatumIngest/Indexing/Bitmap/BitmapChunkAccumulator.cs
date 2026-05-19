using Heliosoph.DatumV.DatumFile;
using Heliosoph.DatumV.Model;
using ZstdSharp;

namespace Heliosoph.DatumV.Indexing.Bitmap;

/// <summary>
/// Per-column, per-chunk accumulator that tracks which rows contain each distinct value.
/// Accumulates bit arrays during row scanning, compresses them at chunk boundaries using
/// a single shared Zstd context, and reuses byte buffers across chunks to minimize
/// allocation pressure. Abandons tracking when the distinct value count exceeds the
/// configured cardinality threshold.
/// </summary>
internal sealed class BitmapChunkAccumulator
{
    private readonly int _cardinalityThreshold;

    /// <summary>
    /// Thread-local reusable Zstd compressor context for bitmap chunk compression.
    /// </summary>
    [ThreadStatic]
    private static Compressor? _threadCompressor;

    /// <summary>
    /// Persistent dictionary mapping each known distinct value to its reusable byte[] bitset.
    /// Retained across chunks — bits are cleared to zero at each <see cref="BeginChunk"/> call
    /// rather than reallocating. This eliminates per-chunk dictionary and byte[] allocation.
    /// </summary>
    private Dictionary<DataValue, byte[]>? _activeBitmaps;

    private int _currentChunkRowCount;
    private int _currentChunkByteCount;
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
    /// Reuses the existing dictionary and byte buffers when possible, clearing
    /// all bits to zero rather than reallocating.
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

        _currentChunkRowCount = chunkRowCapacity;
        _currentChunkByteCount = (chunkRowCapacity + 7) / 8;

        if (_activeBitmaps is null)
        {
            _activeBitmaps = new Dictionary<DataValue, byte[]>();
        }
        else
        {
            // Clear all existing bitmap buffers to zero for the new chunk.
            // This avoids reallocating the dictionary and byte[] arrays.
            foreach (byte[] bits in _activeBitmaps.Values)
            {
                Array.Clear(bits);
            }
        }
    }

    /// <summary>
    /// Records a non-null value at the given row offset within the current chunk.
    /// Abandons tracking if the global distinct count would exceed the threshold, or if
    /// the value is a non-inline reference type (strings &gt;16 UTF-8 bytes) — long strings
    /// are not indexable under the "indexable = self-contained in the DataValue" rule.
    /// </summary>
    /// <param name="value">The cell value (must not be null).</param>
    /// <param name="rowOffsetInChunk">Zero-based row offset within the current chunk.</param>
    internal void Add(DataValue value, int rowOffsetInChunk)
    {
        if (_abandoned || _activeBitmaps is null)
        {
            return;
        }

        if (value.IsNull)
        {
            return;
        }

        // Non-inline String can't be retained as a dictionary key without arena
        // plumbing. Drop the column from the bitmap index the first time we see one.
        if (value.Kind == DataKind.String && !value.IsInline)
        {
            Abandon();
            return;
        }

        if (!_activeBitmaps.TryGetValue(value, out byte[]? bits))
        {
            // Completely new global value. Dictionary count is the current global distinct count.
            if (_activeBitmaps.Count >= _cardinalityThreshold)
            {
                Abandon();
                return;
            }

            bits = new byte[_currentChunkByteCount];
            _activeBitmaps[value] = bits;
        }

        // Inline SetBit to avoid ChunkBitmap struct overhead on the per-row path.
        bits[rowOffsetInChunk >> 3] |= (byte)(1 << (rowOffsetInChunk & 7));
    }

    /// <summary>
    /// Finalizes the current chunk: compresses all populated bitmaps using a single
    /// shared Zstd compression context (amortizing context creation across all values)
    /// and stores the compressed results.
    /// Must be called at each chunk boundary (and after the last row of the final chunk).
    /// </summary>
    /// <param name="actualRowCount">
    /// The actual number of rows in this chunk (may be less than the capacity
    /// passed to <see cref="BeginChunk"/> for the last partial chunk).
    /// </param>
    internal void FinalizeChunk(int actualRowCount)
    {
        if (_abandoned || _activeBitmaps is null)
        {
            return;
        }

        int chunkIndex = _chunkRowCounts.Count;
        _chunkRowCounts.Add(actualRowCount);
        int trimmedByteCount = (actualRowCount + 7) / 8;

        // Reusable compression context — avoids native ZSTD_CCtx allocation per chunk.
        Compressor compressor = (_threadCompressor ??= new Compressor(DatumFileConstants.DefaultZstdCompressionLevel));

        foreach (KeyValuePair<DataValue, byte[]> entry in _activeBitmaps)
        {
            byte[] bits = entry.Value;
            ReadOnlySpan<byte> bitmapSpan = bits.AsSpan(0, trimmedByteCount);

            // Skip values that were not seen in this chunk (all bits are zero).
            if (bitmapSpan.IndexOfAnyExcept((byte)0) < 0)
            {
                // Gap-fill if this value already has compressed entries from prior chunks.
                if (_allChunkCompressed.TryGetValue(entry.Key, out List<byte[]>? existingList)
                    && existingList.Count <= chunkIndex)
                {
                    existingList.Add(Array.Empty<byte>());
                }

                continue;
            }

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

            chunkList.Add(compressor.Wrap(bitmapSpan).ToArray());
        }

        // Gap-fill values in _allChunkCompressed that have fewer entries than expected.
        // This covers values whose _activeBitmaps entry was all-zero in this chunk
        // but were created in _allChunkCompressed during a prior FinalizeChunk.
        foreach (KeyValuePair<DataValue, List<byte[]>> entry in _allChunkCompressed)
        {
            if (entry.Value.Count <= chunkIndex)
            {
                entry.Value.Add(Array.Empty<byte>());
            }
        }
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

    private void Abandon()
    {
        _abandoned = true;
        _activeBitmaps = null;
        _allChunkCompressed.Clear();
        _chunkRowCounts.Clear();
    }
}
