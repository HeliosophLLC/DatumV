namespace DatumIngest.Manifest.CrossManifest.Rules;

using DatumIngest.Manifest.Insights;
using DatumIngest.Manifest.Insights.Rules;

/// <summary>
/// Flags join candidates where the range overlap between numeric key columns is very low
/// or zero. When key value ranges are disjoint, the join will produce few or no matches.
/// </summary>
internal sealed class DisjointRangeRule : ICrossManifestInsightRule
{
    /// <summary>
    /// Range overlap below this fraction triggers the rule.
    /// </summary>
    private const double DisjointRangeMaxOverlap = 0.05;

    /// <inheritdoc />
    public IEnumerable<RawFinding> Evaluate(
        IReadOnlyList<ManifestWithName> manifests,
        IReadOnlyList<JoinCandidate> candidates,
        CrossManifestThresholds thresholds)
    {
        foreach (JoinCandidate candidate in candidates)
        {
            // Only applicable to numeric joins with range overlap data.
            if (!candidate.Evidence.RangeOverlap.HasValue)
            {
                continue;
            }

            double rangeOverlap = candidate.Evidence.RangeOverlap.Value;

            if (rangeOverlap >= DisjointRangeMaxOverlap)
            {
                continue;
            }

            string leftColumns = string.Join(", ", candidate.LeftColumns);
            string rightColumns = string.Join(", ", candidate.RightColumns);

            bool isCompletelyDisjoint = rangeOverlap <= 0.0;

            InsightSeverity severity = isCompletelyDisjoint
                ? InsightSeverity.Critical
                : InsightSeverity.Warning;

            // Very confident when completely disjoint.
            double confidence = isCompletelyDisjoint ? 0.95 : 0.7 + ((DisjointRangeMaxOverlap - rangeOverlap) * 4.0);

            EvidenceBuilder evidence = new EvidenceBuilder()
                .Add(candidate.LeftTable, "rangeOverlap", rangeOverlap)
                .Add(candidate.LeftTable, "leftColumns", leftColumns)
                .Add(candidate.RightTable, "rightColumns", rightColumns);

            string overlapDescription = isCompletelyDisjoint
                ? "completely disjoint value ranges"
                : $"only {rangeOverlap:P1} range overlap";

            yield return new RawFinding(
                InsightKind.DisjointRange,
                InsightCategory.JoinQuality,
                isCompletelyDisjoint ? InsightSeverity.Critical : InsightSeverity.Warning,
                confidence,
                InsightScope.CrossManifest,
                $"Join between '{candidate.LeftTable}' ({leftColumns}) and '{candidate.RightTable}' ({rightColumns}) has {overlapDescription}.",
                isCompletelyDisjoint
                    ? "The join will produce zero rows. This usually indicates incorrect join keys or incompatible data sources."
                    : "The join will match only a small fraction of rows. Most data from both sides will be lost in an inner join.",
                "Verify the join keys are correct and the data sources are compatible. If the ranges differ by units or encoding, apply a transformation before joining.",
                Rationale: null,
                Alternatives: null,
                [candidate.LeftTable, candidate.RightTable],
                [],
                ConflictGroup: null,
                evidence.Build());
        }
    }
}
