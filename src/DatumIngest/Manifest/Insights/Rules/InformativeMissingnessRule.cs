namespace DatumIngest.Manifest.Insights.Rules;

/// <summary>
/// Detects columns where the presence/absence of a value (missingness indicator)
/// likely carries predictive signal. Fires when a column has structured missingness
/// (multiple missing runs) combined with meaningful null ratio.
/// </summary>
internal sealed class InformativeMissingnessRule : IInsightRule
{
    /// <inheritdoc />
    public IEnumerable<RawFinding> Evaluate(QueryResultsManifest manifest, InsightThresholds thresholds)
    {
        foreach (FeatureManifest feature in manifest.Features)
        {
            if (!feature.NullRatio.HasValue || feature.NullRatio.Value <= thresholds.HighMissingnessMinRatio)
            {
                continue;
            }

            // Structured missingness: multiple runs of nulls suggest non-random pattern.
            if (!feature.MissingRuns.HasValue || feature.MissingRuns.Value < 2)
            {
                continue;
            }

            double nullRatio = feature.NullRatio.Value;
            string indicatorAlias = $"{feature.Name}_missing";
            string bundleIdentifier = $"informative-missingness-{feature.Name}";

            EvidenceBuilder evidence = new EvidenceBuilder()
                .Add(feature.Name, "nullRatio", nullRatio)
                .Add(feature.Name, "missingRuns", feature.MissingRuns.Value);

            // Confidence based on how structured the missingness is.
            // Multiple runs + high null ratio → more likely informative.
            double confidence = Math.Min(1.0, 0.5 + (nullRatio * 0.2) + (Math.Min(feature.MissingRuns.Value, 10) * 0.03));

            yield return new RawFinding(
                InsightKind.InformativeMissingness,
                InsightCategory.DataQuality,
                InsightSeverity.Info,
                confidence,
                InsightScope.Feature,
                $"Column '{feature.Name}' has {nullRatio:P1} missing values across {feature.MissingRuns.Value} non-contiguous runs, suggesting structured (non-random) missingness.",
                "If missingness is informative (correlated with the target), discarding it via imputation alone loses a predictive signal.",
                $"Append a missingness indicator column '{indicatorAlias}' alongside imputation of '{feature.Name}'.",
                Rationale: "The null/non-null pattern itself may predict the outcome. A binary indicator preserves this signal even after imputation.",
                Alternatives: null,
                [feature.Name],
                [new InsightAction(
                    ActionKind.Append,
                    Column: null,
                    Expression: $"CASE WHEN [{feature.Name}] IS NULL THEN 1 ELSE 0 END",
                    indicatorAlias,
                    Lossy: false,
                    Reversible: true,
                    bundleIdentifier)],
                ConflictGroup: null,
                evidence.Build());
        }
    }
}
