namespace Axon.QueryEngine.Catalog;

/// <summary>
/// Cost classification for reading a column, used by the query planner
/// to optimize join build-side selection and projection pushdown.
/// </summary>
public enum ColumnCost
{
    /// <summary>Column value is readily available (e.g. in-memory, pre-parsed header).</summary>
    Cheap,

    /// <summary>Column requires some computation or I/O (e.g. parsing a field).</summary>
    Moderate,

    /// <summary>Column requires heavy I/O (e.g. decompressing a ZIP entry).</summary>
    Expensive
}

/// <summary>
/// Describes the operational characteristics of a table provider, enabling
/// the query planner to make cost-based decisions.
/// </summary>
/// <param name="EstimatedRowCount">Estimated number of rows, or null if unknown.</param>
/// <param name="EstimatedRowSizeBytes">Estimated average row size in bytes, or null if unknown.</param>
/// <param name="SupportsSeek">Whether the provider can seek to arbitrary row offsets.</param>
/// <param name="ColumnCosts">Per-column cost classification; columns not listed are assumed <see cref="ColumnCost.Cheap"/>.</param>
public sealed record ProviderCapabilities(
    long? EstimatedRowCount,
    long? EstimatedRowSizeBytes,
    bool SupportsSeek,
    IReadOnlyDictionary<string, ColumnCost> ColumnCosts);
