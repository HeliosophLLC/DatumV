namespace DatumIngest.Manifest.SchemaMatching;

using System.Text.Json.Serialization;

/// <summary>
/// A scored join candidate between two tables, including the matched columns,
/// evidence signals, join classification, and quality warnings.
/// </summary>
public sealed class JoinCandidate
{
    /// <summary>Gets the name of the left table.</summary>
    public required string LeftTable { get; init; }

    /// <summary>Gets the name of the right table.</summary>
    public required string RightTable { get; init; }

    /// <summary>Gets the matched column names from the left table.</summary>
    public required IReadOnlyList<string> LeftColumns { get; init; }

    /// <summary>Gets the matched column names from the right table.</summary>
    public required IReadOnlyList<string> RightColumns { get; init; }

    /// <summary>Gets the per-signal evidence scores for this candidate.</summary>
    public required JoinEvidence Evidence { get; init; }

    /// <summary>Gets the overall confidence in [0, 1].</summary>
    public required double Confidence { get; init; }

    /// <summary>Gets the estimated join cardinality classification.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<JoinClassification>))]
    public required JoinClassification EstimatedJoinType { get; init; }

    /// <summary>Gets the estimated fanout (average rows matched per left row). Null if not estimable.</summary>
    public double? EstimatedFanout { get; init; }

    /// <summary>Gets quality warnings for this join candidate (e.g., "high null-key ratio", "many-to-many risk").</summary>
    public IReadOnlyList<string>? QualityWarnings { get; init; }
}
