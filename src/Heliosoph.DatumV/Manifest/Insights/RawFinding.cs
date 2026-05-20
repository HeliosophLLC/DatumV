namespace Heliosoph.DatumV.Manifest.Insights;

using System.Text.Json;

/// <summary>
/// An unprocessed finding emitted by an <see cref="IInsightRule"/> before clustering,
/// action routing, and apply mode derivation. Internal to the insight pipeline.
/// </summary>
/// <param name="Kind">Machine-readable insight identifier.</param>
/// <param name="Category">Domain category.</param>
/// <param name="Severity">Severity tier.</param>
/// <param name="Confidence">Calibrated confidence in [0, 1].</param>
/// <param name="Scope">Granularity of the finding.</param>
/// <param name="Observation">Factual observation — what the data shows.</param>
/// <param name="Risk">Why this matters for ML.</param>
/// <param name="Recommendation">What to do.</param>
/// <param name="Rationale">Optional deeper explanation.</param>
/// <param name="Alternatives">Optional alternative approaches.</param>
/// <param name="AffectedFeatures">Columns affected by this finding.</param>
/// <param name="Actions">All candidate actions (routing to actions vs. proposedActions happens later).</param>
/// <param name="ConflictGroup">Optional mutual exclusion group.</param>
/// <param name="Evidence">Structured evidence keyed by feature name then statistic name.</param>
internal sealed record RawFinding(
    InsightKind Kind,
    InsightCategory Category,
    InsightSeverity Severity,
    double Confidence,
    InsightScope Scope,
    string Observation,
    string Risk,
    string Recommendation,
    string? Rationale,
    IReadOnlyList<string>? Alternatives,
    IReadOnlyList<string> AffectedFeatures,
    IReadOnlyList<InsightAction> Actions,
    string? ConflictGroup,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>? Evidence);
