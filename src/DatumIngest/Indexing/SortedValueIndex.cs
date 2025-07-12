using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Indexing;

/// <summary>
/// A single entry in a <see cref="SortedValueIndex"/>, mapping a key value
/// to the chunk and row offset where it appears.
/// </summary>
/// <param name="Key">The indexed column value.</param>
/// <param name="ChunkIndex">Zero-based index of the chunk containing this value.</param>
/// <param name="RowOffsetInChunk">Zero-based row offset within the chunk.</param>
public readonly record struct ValueIndexEntry(DataValue Key, int ChunkIndex, long RowOffsetInChunk);

/// <summary>
/// Sorted array of <see cref="ValueIndexEntry"/> for a single column, enabling
/// binary search for equality lookups and range scans. Entries are sorted by key
/// value using <see cref="StatisticsPredicateEvaluator.CompareValues"/> semantics.
/// </summary>
/// <remarks>
/// For columns with high cardinality, the sorted index allows O(log n) key lookup
/// instead of a full scan. Combined with chunk statistics, this enables two-level
/// pruning: first eliminate chunks via min/max stats, then locate exact rows via
/// the sorted index within surviving chunks.
/// </remarks>
public sealed class SortedValueIndex
{
    private readonly ValueIndexEntry[] _entries;

    /// <summary>Number of entries in this index.</summary>
    public int Count => _entries.Length;

    /// <summary>The sorted entries (for serialization and testing).</summary>
    internal ReadOnlySpan<ValueIndexEntry> Entries => _entries;

    /// <summary>
    /// Creates a sorted value index from pre-sorted entries.
    /// </summary>
    /// <param name="entries">Entries sorted by key value. The caller must ensure sort order.</param>
    public SortedValueIndex(ValueIndexEntry[] entries)
    {
        _entries = entries;
    }

    /// <summary>
    /// Searches for the exact key value and returns all matching entries.
    /// Uses binary search with O(log n) initial probe, then scans duplicates.
    /// </summary>
    /// <param name="key">The value to look up.</param>
    /// <returns>All entries whose key equals the search value.</returns>
    public IReadOnlyList<ValueIndexEntry> FindExact(DataValue key)
    {
        int position = BinarySearchFirst(key);

        if (position < 0)
        {
            return Array.Empty<ValueIndexEntry>();
        }

        List<ValueIndexEntry> results = new();

        // Scan forward through all duplicates.
        for (int index = position; index < _entries.Length; index++)
        {
            if (CompareKeys(_entries[index].Key, key) != 0)
            {
                break;
            }

            results.Add(_entries[index]);
        }

        return results;
    }

    /// <summary>
    /// Returns all entries whose key falls within the inclusive range [low, high].
    /// </summary>
    /// <param name="low">Lower bound (inclusive).</param>
    /// <param name="high">Upper bound (inclusive).</param>
    /// <returns>All entries with keys in the specified range.</returns>
    public IReadOnlyList<ValueIndexEntry> FindRange(DataValue low, DataValue high)
    {
        int startPosition = BinarySearchFirstGreaterOrEqual(low);

        if (startPosition >= _entries.Length)
        {
            return Array.Empty<ValueIndexEntry>();
        }

        List<ValueIndexEntry> results = new();

        for (int index = startPosition; index < _entries.Length; index++)
        {
            if (CompareKeys(_entries[index].Key, high) > 0)
            {
                break;
            }

            results.Add(_entries[index]);
        }

        return results;
    }

    /// <summary>
    /// Returns the set of chunk indexes that contain any entry with the given key.
    /// </summary>
    /// <param name="key">The value to look up.</param>
    /// <returns>Distinct chunk indexes containing the key.</returns>
    public IReadOnlySet<int> FindChunksContaining(DataValue key)
    {
        IReadOnlyList<ValueIndexEntry> entries = FindExact(key);
        HashSet<int> chunks = new();

        foreach (ValueIndexEntry entry in entries)
        {
            chunks.Add(entry.ChunkIndex);
        }

        return chunks;
    }

    /// <summary>
    /// Returns the set of chunk indexes that contain entries in the inclusive range.
    /// </summary>
    /// <param name="low">Lower bound (inclusive).</param>
    /// <param name="high">Upper bound (inclusive).</param>
    /// <returns>Distinct chunk indexes with keys in the range.</returns>
    public IReadOnlySet<int> FindChunksInRange(DataValue low, DataValue high)
    {
        IReadOnlyList<ValueIndexEntry> entries = FindRange(low, high);
        HashSet<int> chunks = new();

        foreach (ValueIndexEntry entry in entries)
        {
            chunks.Add(entry.ChunkIndex);
        }

        return chunks;
    }

    /// <summary>
    /// Finds the first entry whose key equals <paramref name="key"/>.
    /// Returns a negative value if not found.
    /// </summary>
    private int BinarySearchFirst(DataValue key)
    {
        int low = 0;
        int high = _entries.Length - 1;
        int result = -1;

        while (low <= high)
        {
            int mid = low + (high - low) / 2;
            int comparison = CompareKeys(_entries[mid].Key, key);

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
                high = mid - 1; // Continue searching left for the first occurrence.
            }
        }

        return result;
    }

    /// <summary>
    /// Finds the index of the first entry whose key is greater than or equal to <paramref name="key"/>.
    /// Returns <see cref="Count"/> if all entries are less than the key.
    /// </summary>
    private int BinarySearchFirstGreaterOrEqual(DataValue key)
    {
        int low = 0;
        int high = _entries.Length;

        while (low < high)
        {
            int mid = low + (high - low) / 2;

            if (CompareKeys(_entries[mid].Key, key) < 0)
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
    /// Sorts an unsorted array of entries in place, producing a valid sorted index.
    /// </summary>
    /// <param name="entries">The entries to sort.</param>
    /// <returns>A new sorted value index.</returns>
    public static SortedValueIndex BuildFromUnsorted(ValueIndexEntry[] entries)
    {
        Array.Sort(entries, (a, b) => CompareKeys(a.Key, b.Key));
        return new SortedValueIndex(entries);
    }

    private static int CompareKeys(DataValue left, DataValue right)
    {
        return StatisticsPredicateEvaluator.CompareValues(left, right);
    }
}
