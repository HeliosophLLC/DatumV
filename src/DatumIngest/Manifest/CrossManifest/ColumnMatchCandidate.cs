namespace DatumIngest.Manifest.CrossManifest;

/// <summary>
/// An intermediate column match candidate before full evidence scoring.
/// Produced during column matching and consumed during evidence scoring.
/// </summary>
/// <param name="LeftColumn">Column name from the left table.</param>
/// <param name="RightColumn">Column name from the right table.</param>
/// <param name="NameSimilarity">Normalized name similarity score in [0, 1].</param>
/// <param name="TypeCompatibility">Type compatibility score (1.0 = exact, 0.5–0.8 = coercible, 0.0 = incompatible).</param>
internal sealed record ColumnMatchCandidate(
    string LeftColumn,
    string RightColumn,
    double NameSimilarity,
    double TypeCompatibility);
