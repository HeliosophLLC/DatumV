using System.IO.MemoryMappedFiles;
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
    private readonly Dictionary<DataValue, byte[][]>? _compressedBitmaps;
    private readonly MemoryMappedViewAccessor? _accessor;
    private readonly Dictionary<DataValue, ChunkLocation[]>? _chunkLocations;
    private readonly int _chunkCount;
    private readonly int[] _chunkRowCounts;
    private readonly DataKind? _keyKind;

    /// <summary>
    /// Describes the position and size of a single compressed bitmap payload within a
    /// memory-mapped index file. Used by the reader to locate compressed data on demand.
    /// </summary>
    internal readonly struct ChunkLocation
    {
        /// <summary>Absolute byte offset of the compressed bitmap data.</summary>
        internal readonly long Offset;

        /// <summary>Length in bytes of the compressed bitmap data (0 = value absent from chunk).</summary>
        internal readonly int Length;

        /// <summary>
        /// Creates a new chunk location descriptor.
        /// </summary>
        internal ChunkLocation(long offset, int length)
        {
            Offset = offset;
            Length = length;
        }
    }

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
        _keyKind = InferKeyKind(compressedBitmaps.Keys);
    }

    /// <summary>
    /// Creates a memory-mapped bitmap column index that reads compressed bitmap payloads
    /// from a <see cref="MemoryMappedViewAccessor"/> on demand instead of holding all
    /// compressed data in managed heap memory.
    /// </summary>
    /// <param name="accessor">The shared view accessor spanning the index file.</param>
    /// <param name="chunkLocations">
    /// Dictionary mapping each distinct value to an array of <see cref="ChunkLocation"/>
    /// descriptors, one per chunk, identifying offset and length of each compressed payload.
    /// </param>
    /// <param name="chunkCount">Number of chunks.</param>
    /// <param name="chunkRowCounts">
    /// Array of row counts per chunk (needed for decompression target size).
    /// </param>
    internal BitmapColumnIndex(
        MemoryMappedViewAccessor accessor,
        Dictionary<DataValue, ChunkLocation[]> chunkLocations,
        int chunkCount,
        int[] chunkRowCounts)
    {
        _accessor = accessor;
        _chunkLocations = chunkLocations;
        _chunkCount = chunkCount;
        _chunkRowCounts = chunkRowCounts;
        _keyKind = InferKeyKind(chunkLocations.Keys);
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<DataValue> DistinctValues =>
        _compressedBitmaps is not null
            ? _compressedBitmaps.Keys
            : _chunkLocations!.Keys;

    /// <inheritdoc/>
    public int ChunkCount => _chunkCount;

    /// <summary>
    /// The row counts per chunk (needed for bitmap decompression and sizing).
    /// </summary>
    internal IReadOnlyList<int> ChunkRowCounts => _chunkRowCounts;

    /// <summary>
    /// The underlying compressed bitmap data for serialization. For memory-mapped
    /// indexes, reads all compressed payloads from the accessor into newly allocated arrays.
    /// </summary>
    internal IReadOnlyDictionary<DataValue, byte[][]> CompressedBitmaps
    {
        get
        {
            if (_compressedBitmaps is not null)
            {
                return _compressedBitmaps;
            }

            Dictionary<DataValue, byte[][]> result = new(_chunkLocations!.Count);

            foreach (KeyValuePair<DataValue, ChunkLocation[]> entry in _chunkLocations)
            {
                byte[][] chunkBitmaps = new byte[entry.Value.Length][];

                for (int chunkIndex = 0; chunkIndex < entry.Value.Length; chunkIndex++)
                {
                    ChunkLocation location = entry.Value[chunkIndex];

                    if (location.Length == 0)
                    {
                        chunkBitmaps[chunkIndex] = [];
                    }
                    else
                    {
                        byte[] compressed = new byte[location.Length];
                        _accessor!.ReadArray(location.Offset, compressed.AsSpan());
                        chunkBitmaps[chunkIndex] = compressed;
                    }
                }

                result[entry.Key] = chunkBitmaps;
            }

            return result;
        }
    }

    /// <inheritdoc/>
    public ChunkBitmap GetChunkBitmap(DataValue value, int chunkIndex)
    {
        if (chunkIndex < 0 || chunkIndex >= _chunkCount)
        {
            return ChunkBitmap.Create(0);
        }

        DataValue normalized = NormalizeKey(value);

        if (_compressedBitmaps is not null)
        {
            return GetChunkBitmapFromHeap(normalized, chunkIndex);
        }

        return GetChunkBitmapFromAccessor(normalized, chunkIndex);
    }

    /// <summary>
    /// Retrieves a chunk bitmap from the in-memory compressed bitmap dictionary.
    /// </summary>
    private ChunkBitmap GetChunkBitmapFromHeap(DataValue value, int chunkIndex)
    {
        if (!_compressedBitmaps!.TryGetValue(value, out byte[][]? chunkBitmaps)
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

    /// <summary>
    /// Retrieves a chunk bitmap by reading compressed bytes from the memory-mapped accessor.
    /// </summary>
    private ChunkBitmap GetChunkBitmapFromAccessor(DataValue value, int chunkIndex)
    {
        if (!_chunkLocations!.TryGetValue(value, out ChunkLocation[]? locations)
            || chunkIndex >= locations.Length)
        {
            return ChunkBitmap.Create(_chunkRowCounts[chunkIndex]);
        }

        ChunkLocation location = locations[chunkIndex];

        if (location.Length == 0)
        {
            return ChunkBitmap.Create(_chunkRowCounts[chunkIndex]);
        }

        byte[] compressed = new byte[location.Length];
        _accessor!.ReadArray(location.Offset, compressed.AsSpan());
        return ChunkBitmap.FromCompressed(compressed, _chunkRowCounts[chunkIndex]);
    }

    /// <inheritdoc/>
    public bool ChunkContainsValue(DataValue value, int chunkIndex)
    {
        DataValue normalized = NormalizeKey(value);

        if (_compressedBitmaps is not null)
        {
            return ChunkContainsValueFromHeap(normalized, chunkIndex);
        }

        return ChunkContainsValueFromAccessor(normalized, chunkIndex);
    }

    /// <summary>
    /// Checks value presence in a chunk using the in-memory compressed bitmap dictionary.
    /// </summary>
    private bool ChunkContainsValueFromHeap(DataValue value, int chunkIndex)
    {
        if (!_compressedBitmaps!.TryGetValue(value, out byte[][]? chunkBitmaps)
            || chunkIndex < 0
            || chunkIndex >= chunkBitmaps.Length)
        {
            return false;
        }

        byte[] compressed = chunkBitmaps[chunkIndex];

        if (compressed.Length == 0)
        {
            return false;
        }

        ChunkBitmap bitmap = ChunkBitmap.FromCompressed(compressed, _chunkRowCounts[chunkIndex]);
        return !bitmap.IsEmpty;
    }

    /// <summary>
    /// Checks value presence in a chunk by reading compressed bytes from the memory-mapped accessor.
    /// </summary>
    private bool ChunkContainsValueFromAccessor(DataValue value, int chunkIndex)
    {
        if (!_chunkLocations!.TryGetValue(value, out ChunkLocation[]? locations)
            || chunkIndex < 0
            || chunkIndex >= locations.Length)
        {
            return false;
        }

        ChunkLocation location = locations[chunkIndex];

        if (location.Length == 0)
        {
            return false;
        }

        byte[] compressed = new byte[location.Length];
        _accessor!.ReadArray(location.Offset, compressed.AsSpan());
        ChunkBitmap bitmap = ChunkBitmap.FromCompressed(compressed, _chunkRowCounts[chunkIndex]);
        return !bitmap.IsEmpty;
    }

    /// <inheritdoc/>
    public IReadOnlySet<int> FindChunksContaining(DataValue value)
    {
        DataValue normalized = NormalizeKey(value);
        HashSet<int> chunks = new();

        if (_compressedBitmaps is not null)
        {
            if (!_compressedBitmaps.TryGetValue(normalized, out byte[][]? chunkBitmaps))
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
        }
        else
        {
            if (!_chunkLocations!.TryGetValue(normalized, out ChunkLocation[]? locations))
            {
                return chunks;
            }

            for (int chunkIndex = 0; chunkIndex < locations.Length; chunkIndex++)
            {
                ChunkLocation location = locations[chunkIndex];

                if (location.Length > 0)
                {
                    byte[] compressed = new byte[location.Length];
                    _accessor!.ReadArray(location.Offset, compressed.AsSpan());
                    ChunkBitmap bitmap = ChunkBitmap.FromCompressed(
                        compressed, _chunkRowCounts[chunkIndex]);

                    if (!bitmap.IsEmpty)
                    {
                        chunks.Add(chunkIndex);
                    }
                }
            }
        }

        return chunks;
    }

    /// <summary>
    /// Coerces a lookup value to match the <see cref="DataKind"/> of keys stored in this
    /// index, ensuring dictionary lookups succeed even when the caller's literal type differs
    /// from the column's storage type (e.g. <see cref="DataKind.Float64"/> literal against
    /// a <see cref="DataKind.Boolean"/> bitmap key).
    /// </summary>
    private DataValue NormalizeKey(DataValue value)
    {
        if (_keyKind is null || value.Kind == _keyKind.Value)
        {
            return value;
        }

        return value.CoerceToKind(_keyKind.Value);
    }

    /// <summary>
    /// Infers the <see cref="DataKind"/> of dictionary keys from the first key.
    /// Returns <c>null</c> when the dictionary is empty.
    /// </summary>
    private static DataKind? InferKeyKind(ICollection<DataValue> keys)
    {
        using IEnumerator<DataValue> enumerator = keys.GetEnumerator();
        return enumerator.MoveNext() ? enumerator.Current.Kind : null;
    }
}
