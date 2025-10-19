using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using DatumIngest.Model;

namespace DatumIngest.Indexing.Sorted;

/// <summary>
/// A memory-mapped <see cref="IColumnIndex"/> for a single column, backed by a fixed-width
/// binary layout (v4 format). Key lookup and range scans use binary search directly on
/// the mapped memory without materializing <see cref="ValueIndexEntry"/> arrays.
/// </summary>
/// <remarks>
/// <para>
/// For numeric and temporal columns, keys are stored in a sort-preserving binary encoding
/// (see <see cref="SortedIndexKeyEncoder"/>), so <c>SequenceCompareTo</c> on raw bytes
/// gives the correct ordering. Binary search operates on encoded byte slices.
/// </para>
/// <para>
/// For string columns, keys store fixed-width (offset, length) references into a string table
/// region. Binary search must dereference the string table for comparison. The string table
/// is also memory-mapped.
/// </para>
/// <para>
/// The locators array stores <c>(int32 ChunkIndex, int64 RowOffsetInChunk)</c> tuples in a
/// fixed 12-byte layout, parallel to the keys array.
/// </para>
/// </remarks>
internal sealed class SortedIndex : IColumnIndex
{
    /// <summary>Fixed size of each locator entry: 4-byte chunk index + 8-byte row offset.</summary>
    internal const int LocatorWidth = 12;

    private readonly MemoryMappedViewAccessor _accessor;
    private readonly DataKind _kind;
    private readonly int _keyWidth;
    private readonly long _entryCount;
    private readonly long _keysOffset;
    private readonly long _locatorsOffset;
    private readonly long _stringTableOffset;
    private readonly long _stringTableLength;

    /// <summary>
    /// Creates a mapped sorted index for a single column.
    /// </summary>
    /// <param name="accessor">View accessor for the memory-mapped file.</param>
    /// <param name="kind">The <see cref="DataKind"/> of the indexed column.</param>
    /// <param name="entryCount">Number of index entries.</param>
    /// <param name="keysOffset">Byte offset of the keys array within the mapped region.</param>
    /// <param name="locatorsOffset">Byte offset of the locators array within the mapped region.</param>
    /// <param name="stringTableOffset">Byte offset of the string table (0 for non-string columns).</param>
    /// <param name="stringTableLength">Byte length of the string table (0 for non-string columns).</param>
    public SortedIndex(
        MemoryMappedViewAccessor accessor,
        DataKind kind,
        long entryCount,
        long keysOffset,
        long locatorsOffset,
        long stringTableOffset,
        long stringTableLength)
    {
        _accessor = accessor;
        _kind = kind;
        _keyWidth = SortedIndexKeyEncoder.GetKeyWidth(kind);
        _entryCount = entryCount;
        _keysOffset = keysOffset;
        _locatorsOffset = locatorsOffset;
        _stringTableOffset = stringTableOffset;
        _stringTableLength = stringTableLength;
    }

    /// <inheritdoc/>
    public long EntryCount => _entryCount;

    /// <inheritdoc/>
    public IReadOnlyList<ValueIndexEntry> FindExact(DataValue key)
    {
        long position = BinarySearchFirst(key);

        if (position < 0)
        {
            return Array.Empty<ValueIndexEntry>();
        }

        List<ValueIndexEntry> results = new();

        for (long index = position; index < _entryCount; index++)
        {
            if (CompareKeyAtIndex(index, key) != 0)
            {
                break;
            }

            results.Add(ReadEntry(index));
        }

        return results;
    }

    /// <inheritdoc/>
    public IReadOnlyList<ValueIndexEntry> FindRange(DataValue low, DataValue high)
    {
        long startPosition = BinarySearchFirstGreaterOrEqual(low);

        if (startPosition >= _entryCount)
        {
            return Array.Empty<ValueIndexEntry>();
        }

        List<ValueIndexEntry> results = new();

        for (long index = startPosition; index < _entryCount; index++)
        {
            if (CompareKeyAtIndex(index, high) > 0)
            {
                break;
            }

            results.Add(ReadEntry(index));
        }

        return results;
    }

    /// <inheritdoc/>
    public IReadOnlySet<int> FindChunksContaining(DataValue key)
    {
        long position = BinarySearchFirst(key);

        if (position < 0)
        {
            return new HashSet<int>();
        }

        HashSet<int> chunks = new();

        for (long index = position; index < _entryCount; index++)
        {
            if (CompareKeyAtIndex(index, key) != 0)
            {
                break;
            }

            chunks.Add(ReadChunkIndex(index));
        }

        return chunks;
    }

    /// <inheritdoc/>
    public IReadOnlySet<int> FindChunksInRange(DataValue low, DataValue high)
    {
        long startPosition = BinarySearchFirstGreaterOrEqual(low);

        if (startPosition >= _entryCount)
        {
            return new HashSet<int>();
        }

        HashSet<int> chunks = new();

        for (long index = startPosition; index < _entryCount; index++)
        {
            if (CompareKeyAtIndex(index, high) > 0)
            {
                break;
            }

            chunks.Add(ReadChunkIndex(index));
        }

        return chunks;
    }

    /// <inheritdoc/>
    public IReadOnlySet<int> FindChunksLessThan(DataValue bound)
    {
        long firstGreaterOrEqual = BinarySearchFirstGreaterOrEqual(bound);
        return CollectChunksFromRange(0, firstGreaterOrEqual);
    }

    /// <inheritdoc/>
    public IReadOnlySet<int> FindChunksLessThanOrEqual(DataValue bound)
    {
        long firstGreaterOrEqual = BinarySearchFirstGreaterOrEqual(bound);

        long end = firstGreaterOrEqual;
        while (end < _entryCount && CompareKeyAtIndex(end, bound) == 0)
        {
            end++;
        }

        return CollectChunksFromRange(0, end);
    }

    /// <inheritdoc/>
    public IReadOnlySet<int> FindChunksGreaterThan(DataValue bound)
    {
        long firstGreaterOrEqual = BinarySearchFirstGreaterOrEqual(bound);

        while (firstGreaterOrEqual < _entryCount && CompareKeyAtIndex(firstGreaterOrEqual, bound) == 0)
        {
            firstGreaterOrEqual++;
        }

        return CollectChunksFromRange(firstGreaterOrEqual, _entryCount);
    }

    /// <inheritdoc/>
    public IReadOnlySet<int> FindChunksGreaterThanOrEqual(DataValue bound)
    {
        long firstGreaterOrEqual = BinarySearchFirstGreaterOrEqual(bound);
        return CollectChunksFromRange(firstGreaterOrEqual, _entryCount);
    }

    /// <inheritdoc/>
    public IEnumerable<ValueIndexEntry> TraverseForward()
    {
        for (long index = 0; index < _entryCount; index++)
        {
            yield return ReadEntry(index);
        }
    }

    /// <inheritdoc/>
    public IEnumerable<ValueIndexEntry> TraverseBackward()
    {
        for (long index = _entryCount - 1; index >= 0; index--)
        {
            yield return ReadEntry(index);
        }
    }

    /// <summary>
    /// Finds the first entry whose key equals <paramref name="key"/>.
    /// Returns a negative value if not found.
    /// </summary>
    private long BinarySearchFirst(DataValue key)
    {
        long low = 0;
        long high = _entryCount - 1;
        long result = -1;

        while (low <= high)
        {
            long mid = low + (high - low) / 2;
            int comparison = CompareKeyAtIndex(mid, key);

            if (comparison < 0)
            {
                low = mid + 1;
            }
            else if (comparison > 0)
            {
                high = mid - 1;
            }
            else
            {
                result = mid;
                high = mid - 1;
            }
        }

        return result;
    }

    /// <summary>
    /// Finds the index of the first entry whose key is greater than or equal to <paramref name="key"/>.
    /// Returns <see cref="EntryCount"/> if all entries are less than the key.
    /// </summary>
    private long BinarySearchFirstGreaterOrEqual(DataValue key)
    {
        long low = 0;
        long high = _entryCount;

        while (low < high)
        {
            long mid = low + (high - low) / 2;

            if (CompareKeyAtIndex(mid, key) < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    /// <summary>
    /// Compares the key at the given index position to <paramref name="searchKey"/>.
    /// For non-string kinds, encodes the search key and does a byte-level comparison.
    /// For string kinds, resolves both keys from the string table and compares ordinally.
    /// </summary>
    private int CompareKeyAtIndex(long index, DataValue searchKey)
    {
        if (_kind == DataKind.String)
        {
            return CompareStringKeyAtIndex(index, searchKey);
        }

        Span<byte> indexKeyBytes = stackalloc byte[_keyWidth];
        ReadKeyBytes(index, indexKeyBytes);

        Span<byte> searchKeyBytes = stackalloc byte[_keyWidth];
        SortedIndexKeyEncoder.Encode(searchKey, searchKeyBytes);

        return indexKeyBytes.SequenceCompareTo(searchKeyBytes);
    }

    /// <summary>
    /// Compares a string key at the given index position against a search key
    /// by resolving both from the string table and comparing ordinally.
    /// </summary>
    private int CompareStringKeyAtIndex(long index, DataValue searchKey)
    {
        string indexString = ReadStringKey(index);
        string searchString = searchKey.AsString();
        return string.Compare(indexString, searchString, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads the raw key bytes for the entry at the given index from the mapped keys array.
    /// </summary>
    private void ReadKeyBytes(long index, Span<byte> destination)
    {
        long offset = _keysOffset + index * _keyWidth;
        _accessor.ReadArray(offset, destination);
    }

    /// <summary>
    /// Reads the string key at the given index by resolving the string table reference.
    /// </summary>
    private string ReadStringKey(long index)
    {
        Span<byte> referenceBytes = stackalloc byte[8];
        long offset = _keysOffset + index * _keyWidth;
        _accessor.ReadArray(offset, referenceBytes);

        (int stringOffset, int stringLength) = SortedIndexKeyEncoder.DecodeStringReference(referenceBytes);

        Span<byte> utf8Bytes = stringLength <= 256
            ? stackalloc byte[stringLength]
            : new byte[stringLength];

        _accessor.ReadArray(_stringTableOffset + stringOffset, utf8Bytes);
        return Encoding.UTF8.GetString(utf8Bytes);
    }

    /// <summary>
    /// Reads a full <see cref="ValueIndexEntry"/> at the given index, reconstructing
    /// the <see cref="DataValue"/> key from the encoded bytes.
    /// </summary>
    private ValueIndexEntry ReadEntry(long index)
    {
        DataValue key = ReadKey(index);
        (int chunkIndex, long rowOffset) = ReadLocator(index);
        return new ValueIndexEntry(key, chunkIndex, rowOffset);
    }

    /// <summary>
    /// Reads and decodes the key at the given index, returning the reconstructed <see cref="DataValue"/>.
    /// </summary>
    private DataValue ReadKey(long index)
    {
        if (_kind == DataKind.String)
        {
            return DataValue.FromString(ReadStringKey(index));
        }

        Span<byte> keyBytes = stackalloc byte[_keyWidth];
        ReadKeyBytes(index, keyBytes);
        return SortedIndexKeyEncoder.Decode(_kind, keyBytes);
    }

    /// <summary>
    /// Reads the chunk index from the locator at the given entry index.
    /// </summary>
    private int ReadChunkIndex(long index)
    {
        long offset = _locatorsOffset + index * LocatorWidth;
        return _accessor.ReadInt32(offset);
    }

    /// <summary>
    /// Reads the full locator (chunk index + row offset) at the given entry index.
    /// </summary>
    private (int ChunkIndex, long RowOffset) ReadLocator(long index)
    {
        long offset = _locatorsOffset + index * LocatorWidth;
        int chunkIndex = _accessor.ReadInt32(offset);
        long rowOffset = _accessor.ReadInt64(offset + 4);
        return (chunkIndex, rowOffset);
    }

    /// <summary>
    /// Collects distinct chunk indexes from a contiguous range of entries.
    /// </summary>
    private IReadOnlySet<int> CollectChunksFromRange(long startInclusive, long endExclusive)
    {
        HashSet<int> chunks = new();

        for (long index = startInclusive; index < endExclusive; index++)
        {
            chunks.Add(ReadChunkIndex(index));
        }

        return chunks;
    }
}

// MemoryMappedViewAccessorExtensions.ReadArray has moved to the parent
// DatumIngest.Indexing namespace so every mmap-backed index reader can use it
// without pulling in the Sorted namespace.
