namespace Heliosoph.DatumV.Catalog;

/// <summary>
/// Validity state of a table's <c>.datum-index</c> acceleration sidecar.
/// Surfaced via <see cref="ITableProvider.GetIndexValidity"/> and through
/// the <c>is_valid</c> column on <c>system.indexes</c> so users
/// can detect tables that need <c>REINDEX</c> without inspecting the
/// file system.
/// </summary>
public enum IndexValidity
{
    /// <summary>
    /// No <c>.datum-index</c> file exists for this table. Indexed
    /// queries fall back to scan; <c>REINDEX</c> creates one. Also the
    /// state for in-memory / virtual / system tables that never
    /// maintain an acceleration sidecar.
    /// </summary>
    Missing = 0,

    /// <summary>
    /// The <c>.datum-index</c> file exists and matches the current
    /// <c>.datum</c> contents. Indexed queries use the acceleration
    /// structures (bloom, bitmap, B+Tree) without visiting unmatched
    /// chunks.
    /// </summary>
    Valid = 1,

    /// <summary>
    /// The <c>.datum-index</c> file exists but is out of sync with the
    /// underlying data — the fingerprint disagrees with the live
    /// <c>.datum</c>, the trailing IDXT tail is torn, or a mutation
    /// invalidated the in-process snapshot. Indexed queries fall back
    /// to scan until <c>REINDEX</c> rebuilds the sidecar.
    /// </summary>
    Stale = 2,
}
