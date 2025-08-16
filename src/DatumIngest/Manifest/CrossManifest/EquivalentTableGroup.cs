namespace DatumIngest.Manifest.CrossManifest;

/// <summary>
/// Identifies a group of tables that share identical or near-identical schemas and connect
/// to the same hub tables, indicating partitions of the same entity (e.g., train/test splits).
/// Only one table from each group is included in the primary join graph; the others appear
/// in alternate <see cref="JoinGraph"/> entries.
/// </summary>
public sealed class EquivalentTableGroup
{
    /// <summary>Gets the names of the tables in this equivalence group.</summary>
    public required IReadOnlyList<string> Tables { get; init; }

    /// <summary>Gets the column names shared across all tables in the group.</summary>
    public required IReadOnlyList<string> SharedColumns { get; init; }

    /// <summary>
    /// Gets the fraction of columns in the smaller table that overlap with the larger table.
    /// A value of 1.0 means identical schemas.
    /// </summary>
    public required double SchemaOverlap { get; init; }

    /// <summary>
    /// Gets the table selected for the primary join graph. This is typically the table
    /// with the strongest aggregate confidence to hub tables.
    /// </summary>
    public required string PreferredTable { get; init; }

    /// <summary>Gets a human-readable explanation of why these tables were grouped.</summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Gets the row count for each table in the group. Keys are table names, values are row counts.
    /// Useful for inferring train/test split ratios.
    /// </summary>
    public required IReadOnlyDictionary<string, long> RowCounts { get; init; }
}
