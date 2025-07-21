namespace DatumIngest.Manifest.Insights.Rules;

/// <summary>
/// Detects columns where nearly all values are unique and no single value dominates,
/// suggesting the column is an identifier (primary key, row ID, UUID) rather than a feature.
/// </summary>
internal sealed class PossibleIdentifierRule : IInsightRule
{
    /// <inheritdoc />
    public IEnumerable<RawFinding> Evaluate(QueryResultsManifest manifest, InsightThresholds thresholds)
    {
        if (manifest.RowCount <= 0)
        {
            yield break;
        }

        foreach (FeatureManifest feature in manifest.Features)
        {
            if (feature.ValidCount == 0)
            {
                continue;
            }

            double distinctRatio = (double)feature.EstimatedDistinctCount / manifest.RowCount;

            if (distinctRatio <= thresholds.PossibleIdentifierMinDistinctRatio)
            {
                continue;
            }

            double topKCoverageRatio = ComputeTopKCoverageRatio(feature.TopKValues, manifest.RowCount);

            if (topKCoverageRatio >= thresholds.PossibleIdentifierMaxTopKCoverage)
            {
                continue;
            }

            // Higher confidence when distinct ratio is very close to 1.0.
            double confidence = Math.Min(1.0, 0.6 + (distinctRatio - thresholds.PossibleIdentifierMinDistinctRatio) * 4.0);

            EvidenceBuilder evidence = new EvidenceBuilder()
                .Add(feature.Name, "estimatedDistinctCount", feature.EstimatedDistinctCount)
                .Add(feature.Name, "distinctRatio", distinctRatio)
                .Add(feature.Name, "topKCoverageRatio", topKCoverageRatio)
                .Add(feature.Name, "rowCount", manifest.RowCount);

            yield return new RawFinding(
                InsightKind.PossibleIdentifier,
                InsightCategory.Dimensionality,
                InsightSeverity.Warning,
                confidence,
                InsightScope.Feature,
                $"Column '{feature.Name}' has {distinctRatio:P1} distinct values relative to row count, with no dominant value (top-K coverage: {topKCoverageRatio:P1}).",
                "Identifier columns have no predictive power — they overfit by memorizing row-level associations. Tree-based models are especially susceptible.",
                $"Drop '{feature.Name}' unless it is needed as a join key or for post-prediction lookups.",
                Rationale: null,
                Alternatives: ["Keep the column if it serves as a group key for evaluation splits."],
                [feature.Name],
                [new InsightAction(
                    ActionKind.Drop,
                    feature.Name,
                    Expression: null,
                    Alias: null,
                    Lossy: true,
                    Reversible: false,
                    BundleIdentifier: null)],
                ConflictGroup: null,
                evidence.Build());
        }
    }

    private static double ComputeTopKCoverageRatio(IReadOnlyList<FrequencyEntry> topKValues, long rowCount)
    {
        if (rowCount == 0 || topKValues.Count == 0)
        {
            return 0.0;
        }

        long totalFrequency = 0;

        foreach (FrequencyEntry entry in topKValues)
        {
            totalFrequency += entry.Frequency;
        }

        return (double)totalFrequency / rowCount;
    }
}
