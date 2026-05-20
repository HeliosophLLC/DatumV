namespace Heliosoph.DatumV.Manifest.Insights;

/// <summary>
/// Interface for a single insight rule that evaluates a manifest and emits zero or more
/// <see cref="RawFinding"/> instances. Each rule encapsulates one detection pattern
/// (e.g., "zero-inflated numeric", "near-duplicate features", "high missingness").
/// </summary>
internal interface IInsightRule
{
    /// <summary>
    /// Evaluates the manifest and emits raw findings. Rules must not perform action routing
    /// or apply mode derivation — that is handled by the orchestrator.
    /// </summary>
    /// <param name="manifest">The query results manifest to analyze.</param>
    /// <param name="thresholds">Configurable thresholds controlling rule sensitivity.</param>
    /// <returns>Zero or more raw findings.</returns>
    IEnumerable<RawFinding> Evaluate(QueryResultsManifest manifest, InsightThresholds thresholds);
}
