namespace DatumIngest.Manifest.CrossManifest;

/// <summary>
/// A transitive join chain through multiple tables (e.g., A → B → C).
/// Represents a path through the join graph where each edge exceeds the confidence threshold.
/// </summary>
/// <param name="Tables">Ordered list of table names along the chain.</param>
/// <param name="Edges">Indexes into <see cref="CrossManifestResult.Candidates"/> for each hop.</param>
/// <param name="MinConfidence">Confidence of the weakest link in the chain.</param>
public sealed record JoinChain(
    IReadOnlyList<string> Tables,
    IReadOnlyList<int> Edges,
    double MinConfidence);
