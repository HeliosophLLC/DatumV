namespace DatumIngest.Manifest.CrossManifest.Rules;

using DatumIngest.Manifest.Insights;
using DatumIngest.Manifest.Insights.Rules;

/// <summary>
/// Flags join candidates where the null-key ratio exceeds the configured threshold.
/// Inner joins will silently drop rows with null keys, potentially losing significant data.
/// </summary>
internal sealed class HighNullKeyRule : ICrossManifestInsightRule
{
    /// <inheritdoc />
    public IEnumerable<RawFinding> Evaluate(
        IReadOnlyList<ManifestWithName> manifests,
        IReadOnlyList<JoinCandidate> candidates,
        CrossManifestThresholds thresholds)
    {
        foreach (JoinCandidate candidate in candidates)
        {
            double nullKeyRatio = candidate.Evidence.NullKeyRatio;

            if (nullKeyRatio <= thresholds.HighNullKeyMinRatio)
            {
                continue;
            }

            string leftColumns = string.Join(", ", candidate.LeftColumns);
            string rightColumns = string.Join(", ", candidate.RightColumns);

            // Confidence scales with how far above the threshold we are.
            double confidence = Math.Min(1.0, 0.6 + (nullKeyRatio * 0.4));

            EvidenceBuilder evidence = new EvidenceBuilder()
                .Add(candidate.LeftTable, "nullKeyRatio", nullKeyRatio)
                .Add(candidate.LeftTable, "leftColumns", leftColumns)
                .Add(candidate.RightTable, "rightColumns", rightColumns);

            yield return new RawFinding(
                InsightKind.HighNullKey,
                InsightCategory.JoinQuality,
                InsightSeverity.Warning,
                confidence,
                InsightScope.CrossManifest,
                $"Join between '{candidate.LeftTable}' ({leftColumns}) and '{candidate.RightTable}' ({rightColumns}) has {nullKeyRatio:P1} null keys.",
                "An inner join will silently drop all rows where the join key is null. If nulls are frequent, this may discard a significant portion of the data.",
                $"Use a LEFT JOIN to preserve rows with null keys, or impute missing key values before joining.",
                Rationale: null,
                Alternatives: ["Investigate why join keys are null — this may indicate upstream data quality issues."],
                [candidate.LeftTable, candidate.RightTable],
                [],
                ConflictGroup: null,
                evidence.Build());
        }
    }
}
