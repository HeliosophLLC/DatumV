namespace DatumIngest.Manifest.CrossManifest;

using DatumIngest.Manifest.Insights;

/// <summary>
/// Interface for a cross-manifest insight rule that evaluates join candidates across
/// multiple manifests and emits zero or more <see cref="RawFinding"/> instances.
/// Each rule encapsulates one cross-table detection pattern
/// (e.g., "many-to-many join", "schema drift", "star schema").
/// </summary>
internal interface ICrossManifestInsightRule
{
    /// <summary>
    /// Evaluates the cross-manifest context and emits raw findings. Rules must not perform
    /// action routing or apply mode derivation — that is handled by the orchestrator.
    /// </summary>
    /// <param name="manifests">Named manifests for all tables under analysis.</param>
    /// <param name="candidates">Scored join candidates.</param>
    /// <param name="thresholds">Configurable thresholds controlling rule sensitivity.</param>
    /// <returns>Zero or more raw findings.</returns>
    IEnumerable<RawFinding> Evaluate(
        IReadOnlyList<ManifestWithName> manifests,
        IReadOnlyList<JoinCandidate> candidates,
        CrossManifestThresholds thresholds);
}
