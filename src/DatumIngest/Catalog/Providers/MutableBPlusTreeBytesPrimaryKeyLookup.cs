using System.Diagnostics.CodeAnalysis;
using Heliosoph.DatumV.Catalog.Executors;
using Heliosoph.DatumV.Indexing;
using Heliosoph.DatumV.Indexing.BTree.MutableBytes;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Catalog.Providers;

/// <summary>
/// Adapts a <see cref="MutableBPlusTreeBytes"/> to the
/// <see cref="IPrimaryKeyLookup"/> surface. The caller
/// (<see cref="InsertExecutor"/>'s PK checker) supplies a
/// composite-encoded byte sequence and gets back a hit/miss answer.
/// </summary>
/// <remarks>
/// Lookups serialise through the underlying tree (single-writer model —
/// the parent provider's mutation lock guarantees no concurrent writes
/// during a check).
/// </remarks>
internal sealed class MutableBPlusTreeBytesPrimaryKeyLookup : IPrimaryKeyLookup
{
    private readonly MutableBPlusTreeBytes _tree;

    internal MutableBPlusTreeBytesPrimaryKeyLookup(MutableBPlusTreeBytes tree)
    {
        _tree = tree;
    }

    /// <inheritdoc/>
    public bool TryFind(ReadOnlySpan<byte> encodedKey, [NotNullWhen(true)] out ValueIndexEntry? entry)
    {
        if (_tree.TryFind(encodedKey, out BytesIndexEntry hit))
        {
            // PK enforcement only consumes (chunk, row); leave the Key
            // field as default(DataValue). The tree stores raw bytes,
            // not typed values — reconstructing a DataValue would
            // require an IValueStore and serves no caller today.
            entry = new ValueIndexEntry(default, hit.ChunkIndex, hit.RowOffsetInChunk);
            return true;
        }
        entry = null;
        return false;
    }
}
