namespace DatumIngest.Manifest.SchemaMatching;

/// <summary>
/// The result of star schema detection across a set of table manifests. Contains
/// the discovered hub tables (each with their spokes) and any tables that could
/// not be placed into a star relationship.
/// </summary>
public sealed class StarSchemaResult
{
    /// <summary>Gets the names of all tables that were analyzed.</summary>
    public required IReadOnlyList<string> Tables { get; init; }

    /// <summary>
    /// Gets the discovered hub tables, ordered by descending spoke count.
    /// Each hub represents one star relationship with its key column(s) and connected spokes.
    /// </summary>
    public required IReadOnlyList<HubTable> Hubs { get; init; }

    /// <summary>
    /// Gets the names of tables that do not appear as a hub or spoke in any discovered star.
    /// These tables may need manual configuration or may be standalone reference data.
    /// </summary>
    public required IReadOnlyList<string> UnmatchedTables { get; init; }
}
