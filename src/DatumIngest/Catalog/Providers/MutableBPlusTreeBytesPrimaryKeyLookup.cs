using System.Diagnostics.CodeAnalysis;
using DatumIngest.Indexing;
using DatumIngest.Indexing.BTree.MutableBytes;
using DatumIngest.Model;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Adapts a <see cref="MutableBPlusTreeBytes"/> to the
/// <see cref="IPrimaryKeyLookup"/> surface in composite-key mode. The
/// caller (<see cref="InsertExecutor"/>'s PK checker) supplies a
/// composite-encoded byte sequence and gets back a hit/miss answer
/// without going through <see cref="DataValue"/>.
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
    public bool IsComposite => true;

    /// <inheritdoc/>
    /// <remarks>
    /// Throws — composite lookups don't accept typed-value probes. Callers
    /// must check <see cref="IsComposite"/> and use
    /// <see cref="TryFindComposite"/> instead.
    /// </remarks>
    public bool TryFind(DataValue key, [NotNullWhen(true)] out ValueIndexEntry? entry)
        => throw new NotSupportedException(
            "This lookup is composite (bytes-keyed). Use TryFindComposite(ReadOnlySpan<byte>, ...) instead.");

    /// <inheritdoc/>
    public bool TryFindComposite(ReadOnlySpan<byte> encodedKey, [NotNullWhen(true)] out ValueIndexEntry? entry)
    {
        if (_tree.TryFind(encodedKey, out BytesIndexEntry hit))
        {
            // PK enforcement only consumes (chunk, row); the Key field is
            // left as default(DataValue) because the bytes-keyed tree
            // stores raw byte arrays, not typed values. Reconstructing a
            // DataValue from the encoded bytes would require an
            // IValueStore and serves no caller today.
            entry = new ValueIndexEntry(default, hit.ChunkIndex, hit.RowOffsetInChunk);
            return true;
        }
        entry = null;
        return false;
    }
}
