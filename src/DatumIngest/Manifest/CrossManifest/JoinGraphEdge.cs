namespace DatumIngest.Manifest.CrossManifest;

using System.Text.Json.Serialization;

/// <summary>
/// Records the primary-graph edge that structurally validated an inherited connection.
/// </summary>
/// <param name="CandidateIndex">
/// Index into <see cref="CrossManifestResult.Candidates"/> of the primary-graph edge.
/// </param>
/// <param name="Confidence">
/// Confidence of the primary-graph edge that met the threshold.
/// </param>
public sealed record InheritedEdgeOrigin(int CandidateIndex, double Confidence);

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
    double Confidence)
{
    /// <summary>
    /// When non-null, indicates this edge was inherited from the primary graph via
    /// equivalent table detection rather than meeting the confidence threshold independently.
    /// The origin records the primary-graph candidate index and confidence so consumers can
    /// correlate back to the structural source in a differently-shaped primary graph.
    /// Consumers filtering edges by confidence should treat inherited edges as structurally
    /// validated regardless of their <see cref="Confidence"/> value.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InheritedEdgeOrigin? InheritedFrom { get; init; }
}
