using DatumIngest.Indexing;
using DatumIngest.Indexing.BTree.MutableBytes;
using DatumIngest.Model;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Adapts a <see cref="MutableBPlusTreeBytes"/> to the
/// <see cref="ICompositeIndex"/> surface that the planner consumes. The
/// adapter holds the descriptor (name + column list) so callers can match
/// predicate columns against the index without re-resolving against the
/// schema, and routes <see cref="FindExact"/> tuples through
/// <see cref="CompositeKeyEncoder"/> before probing the tree.
/// </summary>
/// <remarks>
/// Lookups serialise through the underlying tree (single-writer model —
/// the parent provider's mutation lock guarantees no concurrent writes
/// during a planner-driven read).
/// </remarks>
internal sealed class MutableBPlusTreeBytesCompositeIndex : ICompositeIndex
{
    private readonly MutableBPlusTreeBytes _tree;

    public string Name { get; }
    public IReadOnlyList<string> Columns { get; }

    internal MutableBPlusTreeBytesCompositeIndex(
        MutableBPlusTreeBytes tree, IndexDescriptor descriptor)
    {
        _tree = tree;
        Name = descriptor.Name;
        Columns = descriptor.Columns;
    }

    /// <inheritdoc/>
    public IReadOnlyList<ValueIndexEntry> FindExact(IReadOnlyList<DataValue> tuple)
    {
        if (tuple.Count != Columns.Count)
        {
            throw new ArgumentException(
                $"Composite index '{Name}' expects a tuple of {Columns.Count} values; got {tuple.Count}.",
                nameof(tuple));
        }

        byte[] encoded = CompositeKeyEncoder.Encode(tuple);
        IReadOnlyList<BytesIndexEntry> hits = _tree.FindAll(encoded);
        if (hits.Count == 0) return Array.Empty<ValueIndexEntry>();

        ValueIndexEntry[] result = new ValueIndexEntry[hits.Count];
        for (int i = 0; i < hits.Count; i++)
        {
            BytesIndexEntry hit = hits[i];
            // Key is `default` — the tree stores raw bytes, not typed values,
            // and the planner only uses (ChunkIndex, RowOffsetInChunk) for
            // row seeks. Reconstructing a typed key would require a per-kind
            // decoder and serves no current caller.
            result[i] = new ValueIndexEntry(default, hit.ChunkIndex, hit.RowOffsetInChunk);
        }
        return result;
    }
}
