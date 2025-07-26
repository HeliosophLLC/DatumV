namespace DatumIngest.Manifest.CrossManifest;

/// <summary>
/// An edge in the join graph connecting two tables via a <see cref="JoinCandidate"/>.
/// </summary>
/// <param name="LeftTable">Name of the left table.</param>
/// <param name="RightTable">Name of the right table.</param>
/// <param name="CandidateIndex">Index into the <see cref="CrossManifestResult.Candidates"/> list.</param>
/// <param name="Confidence">Confidence of the underlying join candidate.</param>
public sealed record JoinGraphEdge(
    string LeftTable,
    string RightTable,
    int CandidateIndex,
    double Confidence);
