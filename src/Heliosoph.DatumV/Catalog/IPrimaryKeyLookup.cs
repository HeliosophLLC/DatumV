using System.Diagnostics.CodeAnalysis;
using Heliosoph.DatumV.Indexing;

namespace Heliosoph.DatumV.Catalog;

/// <summary>
/// Read-side surface a provider exposes when it backs PK enforcement with
/// an on-disk index. The lookup is fed composite-encoded byte keys
/// (produced by <see cref="CompositeKeyEncoder"/>); single-column PKs are
/// just the degenerate case of "tuple of one".
/// </summary>
/// <remarks>
/// Providers without an on-disk PK index (TEMP / InMemory) return
/// <see langword="null"/> from <see cref="ITableProvider.GetPrimaryKeyLookup"/>
/// and the executor falls back to the scan-based pre-load path.
/// </remarks>
public interface IPrimaryKeyLookup
{
    /// <summary>
    /// Returns <see langword="true"/> when a row with the given
    /// composite-encoded key exists. The returned
    /// <see cref="ValueIndexEntry.Key"/> is left at <c>default</c> —
    /// the bytes tree stores raw byte arrays, not typed values, and PK
    /// enforcement only consumes <see cref="ValueIndexEntry.ChunkIndex"/> /
    /// <see cref="ValueIndexEntry.RowOffsetInChunk"/>.
    /// </summary>
    bool TryFind(ReadOnlySpan<byte> encodedKey, [NotNullWhen(true)] out ValueIndexEntry? entry);
}
