namespace Heliosoph.DatumV.Manifest.Insights.Rules;

/// <summary>
/// Detects pairs of columns whose null masks are highly correlated, indicating
/// that the same rows tend to be missing across those columns (structured gap).
/// </summary>
internal sealed class CorrelatedMissingnessRule : IInsightRule
{
    /// <inheritdoc />
    public IEnumerable<RawFinding> Evaluate(QueryResultsManifest manifest, InsightThresholds thresholds)
    {
        if (manifest.Interactions is null)
        {
            yield break;
        }

        foreach (ColumnInteraction interaction in manifest.Interactions)
        {
            if (!interaction.MissingnessCorrelation.HasValue)
            {
                continue;
            }

            double correlation = interaction.MissingnessCorrelation.Value;

            if (correlation < thresholds.MissingnessCorrelationMinThreshold)
            {
                continue;
            }

            EvidenceBuilder evidence = new EvidenceBuilder()
                .Add(interaction.ColumnA, "missingnessCorrelation", correlation)
                .Add(interaction.ColumnB, "missingnessCorrelation", correlation);

            // Look up null ratios for context.
            foreach (FeatureManifest feature in manifest.Features)
            {
                if (feature.Name == interaction.ColumnA && feature.NullRatio.HasValue)
                {
                    evidence.Add(interaction.ColumnA, "nullRatio", feature.NullRatio.Value);
                }

                if (feature.Name == interaction.ColumnB && feature.NullRatio.HasValue)
                {
                    evidence.Add(interaction.ColumnB, "nullRatio", feature.NullRatio.Value);
                }
            }

            // Confidence based on correlation strength.
            double confidence = Math.Min(1.0, 0.6 + (correlation * 0.4));

            yield return new RawFinding(
                InsightKind.CorrelatedMissingness,
                InsightCategory.DataQuality,
                InsightSeverity.Info,
                confidence,
                InsightScope.FeaturePair,
                $"Columns '{interaction.ColumnA}' and '{interaction.ColumnB}' have a missingness correlation of {correlation:F3}, meaning they tend to be null on the same rows.",
                "Correlated missingness suggests a structural data gap (e.g., both columns depend on the same upstream process). Independent imputation may understate uncertainty.",
                $"Investigate whether '{interaction.ColumnA}' and '{interaction.ColumnB}' share a causal missingness mechanism. Consider a shared missingness indicator.",
                Rationale: null,
                Alternatives: null,
                [interaction.ColumnA, interaction.ColumnB],
                [],
                ConflictGroup: null,
                evidence.Build());
        }
    }
}
