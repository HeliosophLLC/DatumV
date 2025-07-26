namespace DatumIngest.Manifest.CrossManifest.Rules;

using DatumIngest.Manifest.Insights;
using DatumIngest.Manifest.Insights.Rules;

/// <summary>
/// Flags join candidates classified as many-to-many. These produce Cartesian products
/// that can explode row counts, degrade performance, and introduce duplicated records.
/// </summary>
internal sealed class ManyToManyJoinRule : ICrossManifestInsightRule
{
    /// <inheritdoc />
    public IEnumerable<RawFinding> Evaluate(
        IReadOnlyList<ManifestWithName> manifests,
        IReadOnlyList<JoinCandidate> candidates,
        CrossManifestThresholds thresholds)
    {
        for (int i = 0; i < candidates.Count; i++)
        {
            JoinCandidate candidate = candidates[i];

            if (candidate.EstimatedJoinType != JoinClassification.ManyToMany)
            {
                continue;
            }

            string leftColumns = string.Join(", ", candidate.LeftColumns);
            string rightColumns = string.Join(", ", candidate.RightColumns);

            EvidenceBuilder evidence = new EvidenceBuilder()
                .Add(candidate.LeftTable, "joinType", "ManyToMany")
                .Add(candidate.LeftTable, "leftColumns", leftColumns)
                .Add(candidate.RightTable, "rightColumns", rightColumns)
                .Add(candidate.LeftTable, "confidence", candidate.Confidence);

            if (candidate.EstimatedFanout.HasValue)
            {
                evidence.Add(candidate.LeftTable, "estimatedFanout", candidate.EstimatedFanout.Value);
            }

            string fanoutWarning = candidate.EstimatedFanout.HasValue
                ? $" Estimated fanout is {candidate.EstimatedFanout.Value:F1}x."
                : "";

            yield return new RawFinding(
                InsightKind.ManyToManyJoin,
                InsightCategory.JoinQuality,
                InsightSeverity.Warning,
                candidate.Confidence,
                InsightScope.CrossManifest,
                $"Join between '{candidate.LeftTable}' ({leftColumns}) and '{candidate.RightTable}' ({rightColumns}) is many-to-many.{fanoutWarning}",
                "Many-to-many joins produce Cartesian products that can explode result set size, introduce row duplication, and degrade downstream model quality.",
                $"Add a deduplication step after joining, or introduce an intermediate bridge table. Consider whether a many-to-many relationship is intentional.",
                Rationale: null,
                Alternatives: ["Filter one side to unique keys before joining.", "Aggregate one side to reduce cardinality."],
                [candidate.LeftTable, candidate.RightTable],
                [],
                ConflictGroup: null,
                evidence.Build());
        }
    }
}
