namespace Heliosoph.DatumV.Manifest.Insights;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// A clustered, actionable insight derived from a <see cref="QueryResultsManifest"/>.
/// Each insight separates observation (what the data shows) from risk (why it matters)
/// from recommendation (what to do), with structured patch actions and calibrated confidence.
/// </summary>
public sealed class DatasetInsight
{
    /// <summary>Gets the machine-readable insight identifier.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<InsightKind>))]
    public required InsightKind Kind { get; init; }

    /// <summary>Gets the domain category of this insight.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<InsightCategory>))]
    public required InsightCategory Category { get; init; }

    /// <summary>Gets the severity tier.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<InsightSeverity>))]
    public required InsightSeverity Severity { get; init; }

    /// <summary>Gets the calibrated confidence in [0, 1] based on evidence strength.</summary>
    public required double Confidence { get; init; }

    /// <summary>Gets the granularity of this insight.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<InsightScope>))]
    public required InsightScope Scope { get; init; }

    /// <summary>Gets the factual observation — what the data shows. Only manifest-proven facts.</summary>
    public required string Observation { get; init; }

    /// <summary>Gets the risk statement — why this matters for ML.</summary>
    public required string Risk { get; init; }

    /// <summary>Gets the recommended action — what to do.</summary>
    public required string Recommendation { get; init; }

    /// <summary>Gets an optional deeper explanation or rationale.</summary>
    public string? Rationale { get; init; }

    /// <summary>Gets optional alternative approaches.</summary>
    public IReadOnlyList<string>? Alternatives { get; init; }

    /// <summary>Gets the columns affected by this insight.</summary>
    public required IReadOnlyList<string> AffectedFeatures { get; init; }

    /// <summary>
    /// Gets actions that are executable under the current policy.
    /// Non-empty only when <see cref="RecommendedApplyMode"/> is
    /// <see cref="ApplyMode.AutoSafe"/> or <see cref="ApplyMode.Suggest"/>.
    /// </summary>
    public required IReadOnlyList<InsightAction> Actions { get; init; }

    /// <summary>
    /// Gets actions that require explicit user opt-in or mode escalation.
    /// Contains all actions when <see cref="RecommendedApplyMode"/> is
    /// <see cref="ApplyMode.ManualOnly"/> or <see cref="ApplyMode.Blocked"/>.
    /// </summary>
    public IReadOnlyList<InsightAction>? ProposedActions { get; init; }

    /// <summary>
    /// Gets an optional conflict group identifier. Insights in the same conflict group
    /// are mutually exclusive — the query synthesizer applies the highest-confidence one.
    /// </summary>
    public string? ConflictGroup { get; init; }

    /// <summary>
    /// Gets the computed apply mode, derived from action nature and confidence.
    /// Determines whether actions land in <see cref="Actions"/> or <see cref="ProposedActions"/>.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<ApplyMode>))]
    public required ApplyMode RecommendedApplyMode { get; init; }

    /// <summary>
    /// Gets the structured evidence supporting this insight. Outer key is the feature name,
    /// inner dictionary maps statistic names to their values. Every statistic cited in
    /// <see cref="Observation"/> or <see cref="Recommendation"/> must appear here.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>? Evidence { get; init; }
}
