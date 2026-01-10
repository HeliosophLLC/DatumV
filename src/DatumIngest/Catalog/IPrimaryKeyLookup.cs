using System.Diagnostics.CodeAnalysis;
using DatumIngest.Indexing;
using DatumIngest.Model;

namespace DatumIngest.Catalog;

/// <summary>
/// Read-side surface a provider exposes when it backs PK enforcement with an
/// on-disk index (mutable B+Tree in the <c>.datum-pkindex</c> sidecar).
/// <see cref="InsertExecutor"/>'s PK checker uses it to skip the full-table
/// pre-scan that PR10f's HashSet-based path requires — turning per-INSERT
/// PK enforcement from O(table size) into O(insert size × log table size).
/// Providers without a backing index (TEMP / InMemory, composite PK in PR10h)
/// return <see langword="null"/> from <see cref="ITableProvider.GetPrimaryKeyLookup"/>
/// and the executor falls back to the scan path.
/// </summary>
public interface IPrimaryKeyLookup
{
    /// <summary>
    /// Returns <see langword="true"/> and the matching entry if a row with the
    /// given PK key exists. The entry's chunk + row offset values are not
    /// consumed by PR10h's PK checker (uniqueness only) but the surface keeps
    /// them so a future "lookup the actual row" feature can reuse the same path.
    /// </summary>
    bool TryFind(DataValue key, [NotNullWhen(true)] out ValueIndexEntry? entry);
}
