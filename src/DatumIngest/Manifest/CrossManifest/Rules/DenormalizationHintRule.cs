namespace DatumIngest.Manifest.CrossManifest.Rules;

using DatumIngest.Manifest.Insights;
using DatumIngest.Manifest.Insights.Rules;

/// <summary>
/// Detects tables that share many overlapping columns with similar value distributions,
/// suggesting the data may be denormalized. When data is duplicated across tables, joins
/// may produce redundant columns and storage/compute waste.
/// </summary>
internal sealed class DenormalizationHintRule : ICrossManifestInsightRule
{
    /// <summary>
    /// Minimum fraction of columns that must overlap to flag denormalization.
    /// </summary>
    private const double MinOverlapFraction = 0.5;

    /// <summary>
    /// Minimum TopK Jaccard for columns to be considered value-similar.
    /// </summary>
    private const double MinTopKJaccard = 0.6;

    /// <inheritdoc />
    public IEnumerable<RawFinding> Evaluate(
        IReadOnlyList<ManifestWithName> manifests,
        IReadOnlyList<JoinCandidate> candidates,
        CrossManifestThresholds thresholds)
    {
        // Check pairwise table overlap using the existing candidates.
        // Group candidates by (leftTable, rightTable) and count how many have high value overlap.
        Dictionary<(string Left, string Right), List<JoinCandidate>> groups = new();

        foreach (JoinCandidate candidate in candidates)
        {
            (string Left, string Right) key = (candidate.LeftTable, candidate.RightTable);

            if (!groups.TryGetValue(key, out List<JoinCandidate>? group))
            {
                group = new List<JoinCandidate>();
                groups[key] = group;
            }

            group.Add(candidate);
        }

        foreach (KeyValuePair<(string Left, string Right), List<JoinCandidate>> entry in groups)
        {
            List<JoinCandidate> group = entry.Value;

            // Count candidates with high value overlap (TopK Jaccard + type match).
            int highOverlapCount = 0;

            foreach (JoinCandidate candidate in group)
            {
                if (candidate.Evidence.TopKJaccard >= MinTopKJaccard &&
                    candidate.Evidence.TypeCompatibility >= 1.0)
                {
                    highOverlapCount++;
                }
            }

            if (highOverlapCount < 2)
            {
                continue;
            }

            // Find the smaller table's column count. If both are in manifests,
            // check what fraction of columns overlap.
            ManifestWithName? leftManifest = null;
            ManifestWithName? rightManifest = null;

            foreach (ManifestWithName manifest in manifests)
            {
                if (manifest.Name == entry.Key.Left)
                {
                    leftManifest = manifest;
                }
                else if (manifest.Name == entry.Key.Right)
                {
                    rightManifest = manifest;
                }
            }

            if (leftManifest is null || rightManifest is null)
            {
                continue;
            }

            int smallerColumnCount = Math.Min(
                leftManifest.Manifest.Features.Count,
                rightManifest.Manifest.Features.Count);

            if (smallerColumnCount == 0)
            {
                continue;
            }

            double overlapFraction = (double)highOverlapCount / smallerColumnCount;

            if (overlapFraction < MinOverlapFraction)
            {
                continue;
            }

            EvidenceBuilder evidence = new EvidenceBuilder()
                .Add(entry.Key.Left, "overlappingColumns", highOverlapCount)
                .Add(entry.Key.Left, "leftColumnCount", (long)leftManifest.Manifest.Features.Count)
                .Add(entry.Key.Right, "rightColumnCount", (long)rightManifest.Manifest.Features.Count)
                .Add(entry.Key.Left, "overlapFraction", overlapFraction);

            yield return new RawFinding(
                InsightKind.DenormalizationHint,
                InsightCategory.JoinQuality,
                InsightSeverity.Info,
                Math.Min(1.0, 0.5 + (overlapFraction * 0.5)),
                InsightScope.CrossManifest,
                $"Tables '{entry.Key.Left}' and '{entry.Key.Right}' share {highOverlapCount} columns with similar value distributions ({overlapFraction:P0} of the smaller table).",
                "Duplicated columns across tables indicate denormalization. Joining denormalized tables produces redundant columns and may introduce consistency issues.",
                "Consider whether these tables represent the same entity at different granularities. Remove duplicate columns after joining or normalize the schema.",
                Rationale: null,
                Alternatives: null,
                [entry.Key.Left, entry.Key.Right],
                [],
                ConflictGroup: null,
                evidence.Build());
        }
    }
}
