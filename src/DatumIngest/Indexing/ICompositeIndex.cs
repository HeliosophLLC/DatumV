using DatumIngest.Model;

namespace DatumIngest.Indexing;

/// <summary>
/// Read-side surface a table provider exposes for a user-defined composite
/// secondary index (created via <c>CREATE INDEX name ON table (col1, col2, …)</c>).
/// The query planner consults this surface during predicate matching:
/// AND-chained equality predicates whose column set covers all of the
/// index's declared columns route through <see cref="FindExact"/> for an
/// exact-row seek.
/// </summary>
/// <remarks>
/// Sibling to <see cref="IColumnIndex"/>. The two interfaces are distinct
/// because composite indexes have a multi-column key shape that doesn't
/// fit <c>IColumnIndex</c>'s single-<see cref="DataValue"/> API, and the
/// planner integration logic differs: single-column indexes match a single
/// predicate; composite indexes match a tuple of equality predicates.
/// </remarks>
public interface ICompositeIndex
{
    /// <summary>
    /// The ordered list of columns this index covers. Tuples passed to
    /// <see cref="FindExact"/> must align positionally with this list.
    /// Names are stored in their original CREATE INDEX form; comparison
    /// against schema column names is the caller's concern.
    /// </summary>
    IReadOnlyList<string> Columns { get; }

    /// <summary>
    /// The index's name. Matches the sidecar filename
    /// (<c>.datum-cindex-{Name}</c>) and the catalog descriptor entry.
    /// Exposed for diagnostics / EXPLAIN output.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Returns all entries whose composite key equals <paramref name="tuple"/>.
    /// The tuple's length must match <see cref="Columns"/>; the caller
    /// (planner) supplies values in declared column order. Returned
    /// <see cref="ValueIndexEntry.Key"/> is <see langword="default"/> —
    /// composite indexes store byte-encoded keys, not typed values, and
    /// the planner only consumes <see cref="ValueIndexEntry.ChunkIndex"/> /
    /// <see cref="ValueIndexEntry.RowOffsetInChunk"/> for row seeks.
    /// </summary>
    IReadOnlyList<ValueIndexEntry> FindExact(IReadOnlyList<DataValue> tuple);

    /// <summary>
    /// Returns all entries whose composite key starts with
    /// <paramref name="prefixTuple"/>. The prefix is a leftmost slice of
    /// <see cref="Columns"/> — for an index on <c>(a, b, c)</c>, valid prefixes
    /// are <c>[a]</c>, <c>[a, b]</c>, or the full <c>[a, b, c]</c>. The
    /// adapter encodes only the prefix components and asks the underlying
    /// tree for every entry whose key starts with the resulting byte sequence.
    /// </summary>
    /// <remarks>
    /// This is the leftmost-prefix matching that lets <c>WHERE a = X</c> on a
    /// <c>(a, b)</c> index reach the seek path. For full-tuple matches
    /// prefer <see cref="FindExact"/> — it's a point lookup (cheaper)
    /// where <see cref="FindPrefix"/> is a range scan.
    /// </remarks>
    IReadOnlyList<ValueIndexEntry> FindPrefix(IReadOnlyList<DataValue> prefixTuple);
}
