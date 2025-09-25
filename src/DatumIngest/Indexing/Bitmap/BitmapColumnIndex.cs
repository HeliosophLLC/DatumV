using System.IO.MemoryMappedFiles;
using DatumIngest.Model;

namespace DatumIngest.Indexing.Bitmap;

/// <summary>
/// A bitmap index for a single column, storing one compressed bitset per distinct value
/// per chunk. Implements <see cref="IBitmapColumnIndex"/> with on-demand Zstd decompression.
/// </summary>
/// <remarks>
/// <para>
/// The internal structure is a dictionary mapping each distinct value to an array of
/// compressed byte arrays, one per chunk. <see cref="GetChunkBitmap"/> decompresses on
/// demand. For a column with cardinality <c>C</c> across <c>K</c> chunks, this stores
/// <c>C × K</c> compressed bitsets, each representing which rows in a given chunk contain
/// the corresponding value.
/// </para>
/// <para>
/// String and JSON-keyed bitmap indexes use <see cref="string"/>-keyed dictionaries so
/// that lookups are independent of <see cref="ReferenceStore"/> scope. This is necessary
/// because the index is cached long-term in the catalog while query scopes are per-request.
/// Non-string keys (boolean, numeric, date, etc.) use <see cref="DataValue"/>-keyed
/// dictionaries since their equality and hashing use only inline bit fields.
/// </para>
/// </remarks>
internal sealed class BitmapColumnIndex : IBitmapColumnIndex
{
    // Non-string heap path (boolean, numeric, date keys).
    private readonly Dictionary<DataValue, byte[][]>? _compressedBitmaps;
    // Non-string mapped path.
    private readonly Dictionary<DataValue, ChunkLocation[]>? _chunkLocations;

    // String/JSON heap path — scope-independent.
    private readonly Dictionary<string, byte[][]>? _stringCompressedBitmaps;
    // String/JSON mapped path — scope-independent.
    private readonly Dictionary<string, ChunkLocation[]>? _stringChunkLocations;

    private readonly MemoryMappedViewAccessor? _accessor;
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
        _chunkCount = chunkCount;
        _chunkRowCounts = chunkRowCounts;
        _keyKind = InferKeyKind(compressedBitmaps.Keys);

        if (_keyKind is DataKind.String or DataKind.JsonValue)
        {
            _stringCompressedBitmaps = ConvertToStringKeys(compressedBitmaps);
        }
        else
        {
            _compressedBitmaps = compressedBitmaps;
        }
    }

    /// <summary>
    /// Creates a bitmap column index from string-keyed pre-compressed bitsets.
    /// Used when the caller already has string keys (e.g. deserialization).
    /// </summary>
    internal BitmapColumnIndex(
        DataKind keyKind,
        Dictionary<string, byte[][]> stringCompressedBitmaps,
        int chunkCount,
        int[] chunkRowCounts)
    {
        _chunkCount = chunkCount;
        _chunkRowCounts = chunkRowCounts;
        _keyKind = keyKind;
        _stringCompressedBitmaps = stringCompressedBitmaps;
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
        _chunkCount = chunkCount;
        _chunkRowCounts = chunkRowCounts;
        _keyKind = InferKeyKind(chunkLocations.Keys);

        if (_keyKind is DataKind.String or DataKind.JsonValue)
        {
            _stringChunkLocations = ConvertToStringKeys(chunkLocations);
        }
        else
        {
            _chunkLocations = chunkLocations;
        }
    }

    /// <summary>
    /// Creates a memory-mapped bitmap column index from string-keyed chunk locations.
    /// Used when the caller already has string keys (e.g. deserialization).
    /// </summary>
    internal BitmapColumnIndex(
        MemoryMappedViewAccessor accessor,
        DataKind keyKind,
        Dictionary<string, ChunkLocation[]> stringChunkLocations,
        int chunkCount,
        int[] chunkRowCounts)
    {
        _accessor = accessor;
        _chunkCount = chunkCount;
        _chunkRowCounts = chunkRowCounts;
        _keyKind = keyKind;
        _stringChunkLocations = stringChunkLocations;
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<DataValue> DistinctValues
    {
        get
        {
            if (_compressedBitmaps is not null)
                return _compressedBitmaps.Keys;
            if (_chunkLocations is not null)
                return _chunkLocations.Keys;

            // Reconstruct DataValues from string keys. Only used by the writer
            // (inside a query scope) and tests — allocation is acceptable here.
            IEnumerable<string> keys = _stringCompressedBitmaps is not null
                ? _stringCompressedBitmaps.Keys
                : _stringChunkLocations!.Keys;

            return keys.Select(s => _keyKind == DataKind.JsonValue
                ? DataValue.FromJsonValue(s)
                : DataValue.FromString(s)).ToList();
        }
    }

    /// <inheritdoc/>
    public int ChunkCount => _chunkCount;

    /// <summary>
    /// The row counts per chunk (needed for bitmap decompression and sizing).
    /// </summary>
    internal IReadOnlyList<int> ChunkRowCounts => _chunkRowCounts;

    /// <summary>
    /// The underlying compressed bitmap data for serialization. For memory-mapped
    /// indexes, reads all compressed payloads from the accessor into newly allocated arrays.
    /// For string-keyed indexes, reconstructs <see cref="DataValue"/> keys on the fly.
    /// </summary>
    internal IReadOnlyDictionary<DataValue, byte[][]> CompressedBitmaps
    {
        get
        {
            if (_compressedBitmaps is not null)
            {
                return _compressedBitmaps;
            }

            if (_stringCompressedBitmaps is not null)
            {
                return ReconstructDataValueKeys(_stringCompressedBitmaps);
            }

            if (_chunkLocations is not null)
            {
                return ReadAllFromAccessor(_chunkLocations);
            }

            return ReadAllFromAccessor(_stringChunkLocations!);
        }
    }

    private Dictionary<DataValue, byte[][]> ReconstructDataValueKeys(
        Dictionary<string, byte[][]> source)
    {
        Dictionary<DataValue, byte[][]> result = new(source.Count);

        foreach (KeyValuePair<string, byte[][]> entry in source)
        {
            DataValue key = _keyKind == DataKind.JsonValue
                ? DataValue.FromJsonValue(entry.Key)
                : DataValue.FromString(entry.Key);
            result[key] = entry.Value;
        }

        return result;
    }

    private Dictionary<DataValue, byte[][]> ReadAllFromAccessor<TKey>(
        Dictionary<TKey, ChunkLocation[]> locations) where TKey : notnull
    {
        Dictionary<DataValue, byte[][]> result = new(locations.Count);

        foreach (KeyValuePair<TKey, ChunkLocation[]> entry in locations)
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

            DataValue key = entry.Key switch
            {
                DataValue dv => dv,
                string s => _keyKind == DataKind.JsonValue
                    ? DataValue.FromJsonValue(s)
                    : DataValue.FromString(s),
                _ => throw new InvalidOperationException("Unexpected key type."),
            };

            result[key] = chunkBitmaps;
        }

        return result;
    }

    /// <inheritdoc/>
    public ChunkBitmap GetChunkBitmap(DataValue value, int chunkIndex)
    {
        if (chunkIndex < 0 || chunkIndex >= _chunkCount)
        {
            return ChunkBitmap.Create(0);
        }

        DataValue normalized = NormalizeKey(value);

        if (_stringCompressedBitmaps is not null)
        {
            return GetChunkBitmapFromStringHeap(normalized.AsString(), chunkIndex);
        }

        if (_compressedBitmaps is not null)
        {
            return GetChunkBitmapFromHeap(normalized, chunkIndex);
        }

        if (_stringChunkLocations is not null)
        {
            return GetChunkBitmapFromStringAccessor(normalized.AsString(), chunkIndex);
        }

        return GetChunkBitmapFromAccessor(normalized, chunkIndex);
    }

    private ChunkBitmap GetChunkBitmapFromStringHeap(string key, int chunkIndex)
    {
        if (!_stringCompressedBitmaps!.TryGetValue(key, out byte[][]? chunkBitmaps)
            || chunkIndex >= chunkBitmaps.Length)
        {
            return ChunkBitmap.Create(_chunkRowCounts[chunkIndex]);
        }

        byte[] compressed = chunkBitmaps[chunkIndex];
        return compressed.Length == 0
            ? ChunkBitmap.Create(_chunkRowCounts[chunkIndex])
            : ChunkBitmap.FromCompressed(compressed, _chunkRowCounts[chunkIndex]);
    }

    private ChunkBitmap GetChunkBitmapFromHeap(DataValue value, int chunkIndex)
    {
        if (!_compressedBitmaps!.TryGetValue(value, out byte[][]? chunkBitmaps)
            || chunkIndex >= chunkBitmaps.Length)
        {
            return ChunkBitmap.Create(_chunkRowCounts[chunkIndex]);
        }

        byte[] compressed = chunkBitmaps[chunkIndex];
        return compressed.Length == 0
            ? ChunkBitmap.Create(_chunkRowCounts[chunkIndex])
            : ChunkBitmap.FromCompressed(compressed, _chunkRowCounts[chunkIndex]);
    }

    private ChunkBitmap GetChunkBitmapFromStringAccessor(string key, int chunkIndex)
    {
        if (!_stringChunkLocations!.TryGetValue(key, out ChunkLocation[]? locations)
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
        if (chunkIndex < 0 || chunkIndex >= _chunkCount)
        {
            return false;
        }

        DataValue normalized = NormalizeKey(value);

        if (_stringCompressedBitmaps is not null)
        {
            return ChunkContainsValueFromDict(_stringCompressedBitmaps, normalized.AsString(), chunkIndex);
        }

        if (_compressedBitmaps is not null)
        {
            return ChunkContainsValueFromDict(_compressedBitmaps, normalized, chunkIndex);
        }

        if (_stringChunkLocations is not null)
        {
            return ChunkContainsValueFromAccessor(_stringChunkLocations, normalized.AsString(), chunkIndex);
        }

        return ChunkContainsValueFromAccessor(_chunkLocations!, normalized, chunkIndex);
    }

    private bool ChunkContainsValueFromDict<TKey>(
        Dictionary<TKey, byte[][]> dict, TKey key, int chunkIndex) where TKey : notnull
    {
        if (!dict.TryGetValue(key, out byte[][]? chunkBitmaps)
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

    private bool ChunkContainsValueFromAccessor<TKey>(
        Dictionary<TKey, ChunkLocation[]> dict, TKey key, int chunkIndex) where TKey : notnull
    {
        if (!dict.TryGetValue(key, out ChunkLocation[]? locations)
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

        if (_stringCompressedBitmaps is not null)
        {
            FindChunksFromDict(_stringCompressedBitmaps, normalized.AsString(), chunks);
        }
        else if (_compressedBitmaps is not null)
        {
            FindChunksFromDict(_compressedBitmaps, normalized, chunks);
        }
        else if (_stringChunkLocations is not null)
        {
            FindChunksFromAccessor(_stringChunkLocations, normalized.AsString(), chunks);
        }
        else
        {
            FindChunksFromAccessor(_chunkLocations!, normalized, chunks);
        }

        return chunks;
    }

    private void FindChunksFromDict<TKey>(
        Dictionary<TKey, byte[][]> dict, TKey key, HashSet<int> chunks) where TKey : notnull
    {
        if (!dict.TryGetValue(key, out byte[][]? chunkBitmaps))
        {
            return;
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

    private void FindChunksFromAccessor<TKey>(
        Dictionary<TKey, ChunkLocation[]> dict, TKey key, HashSet<int> chunks) where TKey : notnull
    {
        if (!dict.TryGetValue(key, out ChunkLocation[]? locations))
        {
            return;
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

    /// <summary>
    /// Converts a <see cref="DataValue"/>-keyed dictionary to a <see cref="string"/>-keyed
    /// dictionary. Must be called at construction time while the <see cref="ReferenceStore"/>
    /// that holds the key strings is still active.
    /// </summary>
    private static Dictionary<string, TValue> ConvertToStringKeys<TValue>(
        Dictionary<DataValue, TValue> source)
    {
        Dictionary<string, TValue> result = new(source.Count, StringComparer.Ordinal);

        foreach (KeyValuePair<DataValue, TValue> entry in source)
        {
            result[entry.Key.AsString()] = entry.Value;
        }

        return result;
    }
}
