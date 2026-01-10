using System.Diagnostics.CodeAnalysis;
using DatumIngest.Indexing;
using DatumIngest.Indexing.BTree.Mutable;
using DatumIngest.Model;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Adapts a <see cref="MutableBPlusTree"/> to the <see cref="IPrimaryKeyLookup"/>
/// surface so <see cref="InsertExecutor"/> can probe the on-disk index without
/// taking a dependency on the B+Tree internals. Lookups serialise through the
/// underlying tree (single-writer model — the parent provider's mutation lock
/// already guarantees no concurrent writes during a check).
/// </summary>
internal sealed class MutableBPlusTreePrimaryKeyLookup : IPrimaryKeyLookup
{
    private readonly MutableBPlusTree _tree;

    internal MutableBPlusTreePrimaryKeyLookup(MutableBPlusTree tree)
    {
        _tree = tree;
    }

    /// <inheritdoc/>
    public bool TryFind(DataValue key, [NotNullWhen(true)] out ValueIndexEntry? entry)
    {
        if (_tree.TryFind(key, out ValueIndexEntry hit))
        {
            entry = hit;
            return true;
        }
        entry = null;
        return false;
    }
}
