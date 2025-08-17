namespace DatumIngest.Manifest.SchemaMatching;

/// <summary>
/// A spoke (fact/child) table connected to a <see cref="HubTable"/> via a foreign key
/// relationship. The hub holds unique key values; the spoke holds duplicates referencing
/// those values.
/// </summary>
public sealed class SpokeTable
{
    /// <summary>Gets the name of the spoke table.</summary>
    public required string TableName { get; init; }

    /// <summary>
    /// Gets the columns in the spoke table that reference the hub's <see cref="HubTable.KeyColumns"/>.
    /// Order corresponds to the hub's key columns.
    /// </summary>
    public required IReadOnlyList<string> ForeignKeyColumns { get; init; }

    /// <summary>Gets the confidence score for this hub-to-spoke relationship.</summary>
    public required double Confidence { get; init; }

    /// <summary>Gets the estimated join cardinality classification.</summary>
    public required JoinClassification JoinClassification { get; init; }
}
