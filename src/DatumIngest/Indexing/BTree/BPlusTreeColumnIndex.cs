using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Indexing.Sorted;

namespace DatumIngest.Indexing.BTree;

/// <summary>
/// Implements <see cref="IColumnIndex"/> by delegating to a <see cref="BPlusTreeReader"/>.
/// This adapter allows query operators and the planner to consume B+Tree indexes
/// through the same interface as <see cref="SortedIndex"/>.
/// </summary>
internal sealed class BPlusTreeColumnIndex : IColumnIndex
{
    private readonly BPlusTreeReader _reader;

    /// <summary>
    /// Creates a column index backed by the given B+Tree reader.
    /// </summary>
    /// <param name="reader">The B+Tree reader that serves lookups.</param>
    internal BPlusTreeColumnIndex(BPlusTreeReader reader)
    {
        _reader = reader;
    }

    /// <summary>The underlying B+Tree reader (for serialization and testing).</summary>
    internal BPlusTreeReader Reader => _reader;

    /// <inheritdoc/>
    public long EntryCount => _reader.EntryCount;

    /// <inheritdoc/>
    public IReadOnlyList<ValueIndexEntry> FindExact(DataValue key)
    {
        return _reader.FindExact(key);
    }

    /// <inheritdoc/>
    public IReadOnlyList<ValueIndexEntry> FindRange(DataValue low, DataValue high)
    {
        return _reader.FindRange(low, high);
    }

    /// <inheritdoc/>
    public IReadOnlySet<int> FindChunksContaining(DataValue key)
    {
        return CollectChunks(_reader.FindExact(key));
    }

    /// <inheritdoc/>
    public IReadOnlySet<int> FindChunksInRange(DataValue low, DataValue high)
    {
        return CollectChunks(_reader.FindRange(low, high));
    }

    /// <inheritdoc/>
    public IReadOnlySet<int> FindChunksLessThan(DataValue bound)
    {
        // Find all entries with keys < bound by ranging from the start of the tree up to
        // (but not including) bound. Walk the leaf chain from the first leaf and stop
        // before reaching bound.
        HashSet<int> chunks = new();

        foreach (ValueIndexEntry entry in _reader.TraverseForward())
        {
            if (CompareKeys(entry.Key, bound) >= 0)
            {
                break;
            }

            chunks.Add(entry.ChunkIndex);
        }

        return chunks;
    }

    /// <inheritdoc/>
    public IReadOnlySet<int> FindChunksLessThanOrEqual(DataValue bound)
    {
        HashSet<int> chunks = new();

        foreach (ValueIndexEntry entry in _reader.TraverseForward())
        {
            if (CompareKeys(entry.Key, bound) > 0)
            {
                break;
            }

            chunks.Add(entry.ChunkIndex);
        }

        return chunks;
    }

    /// <inheritdoc/>
    public IReadOnlySet<int> FindChunksGreaterThan(DataValue bound)
    {
        HashSet<int> chunks = new();

        foreach (ValueIndexEntry entry in _reader.TraverseBackward())
        {
            if (CompareKeys(entry.Key, bound) <= 0)
            {
                break;
            }

            chunks.Add(entry.ChunkIndex);
        }

        return chunks;
    }

    /// <inheritdoc/>
    public IReadOnlySet<int> FindChunksGreaterThanOrEqual(DataValue bound)
    {
        HashSet<int> chunks = new();

        foreach (ValueIndexEntry entry in _reader.TraverseBackward())
        {
            if (CompareKeys(entry.Key, bound) < 0)
            {
                break;
            }

            chunks.Add(entry.ChunkIndex);
        }

        return chunks;
    }

    /// <inheritdoc/>
    public IEnumerable<ValueIndexEntry> TraverseForward()
    {
        return _reader.TraverseForward();
    }

    /// <inheritdoc/>
    public IEnumerable<ValueIndexEntry> TraverseBackward()
    {
        return _reader.TraverseBackward();
    }

    private static HashSet<int> CollectChunks(IReadOnlyList<ValueIndexEntry> entries)
    {
        HashSet<int> chunks = new();

        foreach (ValueIndexEntry entry in entries)
        {
            chunks.Add(entry.ChunkIndex);
        }

        return chunks;
    }

    /// <summary>
    /// Compares a decoded key against a caller-supplied bound.
    /// </summary>
    /// <remarks>
    /// Phase 2 of the arena-safe retention work will upgrade this to the arena-aware
    /// <see cref="DataValueComparer.Compare(DataValue, Arena, DataValue, Arena)"/> so that
    /// non-inline string keys compare correctly against non-inline string bounds.
    /// For now, inline strings and fixed-size scalars work correctly; non-inline strings
    /// throw (same as the underlying <see cref="DataValueComparer.Compare(DataValue, DataValue)"/>).
    /// </remarks>
    private static int CompareKeys(DataValue left, DataValue right)
        => StatisticsPredicateEvaluator.CompareValues(left, right);
}
