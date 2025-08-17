namespace DatumIngest.Manifest.CrossManifest;

/// <summary>
/// Summarises the structural complexity of a join graph. Used to detect
/// pathologically dense graphs and surface warnings to the user.
/// </summary>
/// <param name="EdgeCount">Total number of edges in the graph.</param>
/// <param name="TableCount">Number of distinct tables connected by the graph.</param>
/// <param name="MaxEdgesPerTablePair">
/// Highest number of edges between any single pair of tables.
/// Values above 1 indicate ambiguity in the join key choice.
/// </param>
/// <param name="AmbiguityRatio">
/// Ratio of actual edges to the maximum possible edges for the table count:
/// <c>EdgeCount / (TableCount × (TableCount − 1) / 2)</c>.
/// A fully connected graph scores 1.0; a sparse tree approaches 0.0.
/// </param>
public sealed record GraphComplexity(
    int EdgeCount,
    int TableCount,
    int MaxEdgesPerTablePair,
    double AmbiguityRatio);
