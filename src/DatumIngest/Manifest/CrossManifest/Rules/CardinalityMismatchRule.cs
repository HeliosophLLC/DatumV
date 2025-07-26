namespace DatumIngest.Manifest.CrossManifest.Rules;

using DatumIngest.Manifest.Insights;
using DatumIngest.Manifest.Insights.Rules;

/// <summary>
/// Flags join candidates where the cardinality ratio between join keys is extremely low,
/// indicating a large mismatch in distinct value counts. This often signals incorrect join
/// keys or a need for aggregation before joining.
/// </summary>
internal sealed class CardinalityMismatchRule : ICrossManifestInsightRule
{
    /// <inheritdoc />
    public IEnumerable<RawFinding> Evaluate(
        IReadOnlyList<ManifestWithName> manifests,
        IReadOnlyList<JoinCandidate> candidates,
        CrossManifestThresholds thresholds)
    {
        foreach (JoinCandidate candidate in candidates)
        {
            double cardinalityRatio = candidate.Evidence.CardinalityRatio;

            if (cardinalityRatio >= thresholds.CardinalityMismatchMinRatio)
            {
                continue;
            }

            string leftColumns = string.Join(", ", candidate.LeftColumns);
            string rightColumns = string.Join(", ", candidate.RightColumns);

            // Low cardinality ratio means extreme mismatch — high confidence in the problem.
            double confidence = Math.Min(1.0, 0.8 + ((1.0 - cardinalityRatio) * 0.2));

            EvidenceBuilder evidence = new EvidenceBuilder()
                .Add(candidate.LeftTable, "cardinalityRatio", cardinalityRatio)
                .Add(candidate.LeftTable, "leftColumns", leftColumns)
                .Add(candidate.RightTable, "rightColumns", rightColumns);

            yield return new RawFinding(
                InsightKind.CardinalityMismatch,
                InsightCategory.JoinQuality,
                InsightSeverity.Info,
                confidence,
                InsightScope.CrossManifest,
                $"Join between '{candidate.LeftTable}' ({leftColumns}) and '{candidate.RightTable}' ({rightColumns}) has a cardinality ratio of {cardinalityRatio:F3} — one side has far fewer distinct key values.",
                "A large cardinality mismatch can indicate incorrect join keys, missing lookup values, or the need for pre-aggregation. The join may silently drop rows from the high-cardinality side.",
                $"Verify the join keys are correct. If intentional, consider aggregating the high-cardinality side before joining.",
                Rationale: null,
                Alternatives: null,
                [candidate.LeftTable, candidate.RightTable],
                [],
                ConflictGroup: null,
                evidence.Build());
        }
    }
}
