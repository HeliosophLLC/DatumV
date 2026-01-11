using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Indexing.BTree.Mutable;

/// <summary>
/// Adapts a <see cref="MutableBPlusTree"/> to the <see cref="IColumnIndex"/>
/// interface that the query planner consumes. The tree must have been opened
/// with <c>allowDuplicates: true</c> (acceleration mode); chunk-set queries
/// return distinct chunk indexes derived from the matching entries.
/// </summary>
internal sealed class MutableBPlusTreeColumnIndex : IColumnIndex
{
    private readonly MutableBPlusTree _tree;

    internal MutableBPlusTreeColumnIndex(MutableBPlusTree tree)
    {
        _tree = tree;
    }

    /// <inheritdoc/>
    public long EntryCount => _tree.EntryCount;

    /// <inheritdoc/>
    public IReadOnlyList<ValueIndexEntry> FindExact(DataValue key) => _tree.FindAll(key);

    /// <inheritdoc/>
    public IReadOnlyList<ValueIndexEntry> FindRange(DataValue low, DataValue high) => _tree.FindRange(low, high);

    /// <inheritdoc/>
    public IReadOnlySet<int> FindChunksContaining(DataValue key)
    {
        HashSet<int> chunks = new();
        foreach (ValueIndexEntry entry in _tree.FindAll(key))
        {
            chunks.Add(entry.ChunkIndex);
        }
        return chunks;
    }

    /// <inheritdoc/>
    public IReadOnlySet<int> FindChunksInRange(DataValue low, DataValue high)
    {
        HashSet<int> chunks = new();
        foreach (ValueIndexEntry entry in _tree.FindRange(low, high))
        {
            chunks.Add(entry.ChunkIndex);
        }
        return chunks;
    }

    /// <inheritdoc/>
    public IReadOnlySet<int> FindChunksLessThan(DataValue bound)
    {
        HashSet<int> chunks = new();
        foreach (ValueIndexEntry entry in _tree.TraverseForward())
        {
            if (StatisticsPredicateEvaluator.CompareValues(entry.Key, bound) >= 0) break;
            chunks.Add(entry.ChunkIndex);
        }
        return chunks;
    }

    /// <inheritdoc/>
    public IReadOnlySet<int> FindChunksLessThanOrEqual(DataValue bound)
    {
        HashSet<int> chunks = new();
        foreach (ValueIndexEntry entry in _tree.TraverseForward())
        {
            if (StatisticsPredicateEvaluator.CompareValues(entry.Key, bound) > 0) break;
            chunks.Add(entry.ChunkIndex);
        }
        return chunks;
    }

    /// <inheritdoc/>
    public IReadOnlySet<int> FindChunksGreaterThan(DataValue bound)
    {
        HashSet<int> chunks = new();
        foreach (ValueIndexEntry entry in _tree.TraverseBackward())
        {
            if (StatisticsPredicateEvaluator.CompareValues(entry.Key, bound) <= 0) break;
            chunks.Add(entry.ChunkIndex);
        }
        return chunks;
    }

    /// <inheritdoc/>
    public IReadOnlySet<int> FindChunksGreaterThanOrEqual(DataValue bound)
    {
        HashSet<int> chunks = new();
        foreach (ValueIndexEntry entry in _tree.TraverseBackward())
        {
            if (StatisticsPredicateEvaluator.CompareValues(entry.Key, bound) < 0) break;
            chunks.Add(entry.ChunkIndex);
        }
        return chunks;
    }

    /// <inheritdoc/>
    public IEnumerable<ValueIndexEntry> TraverseForward() => _tree.TraverseForward();

    /// <inheritdoc/>
    public IEnumerable<ValueIndexEntry> TraverseBackward() => _tree.TraverseBackward();
}
