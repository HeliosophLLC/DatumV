namespace DatumIngest.Manifest.Insights.Rules;

/// <summary>
/// Detects numeric columns where the range (max − min) is disproportionately large
/// relative to the mean, indicating the need for normalization or standardization.
/// </summary>
internal sealed class NormalizationRule : IInsightRule
{
    /// <inheritdoc />
    public IEnumerable<RawFinding> Evaluate(QueryResultsManifest manifest, InsightThresholds thresholds)
    {
        foreach (FeatureManifest feature in manifest.Features)
        {
            if (feature is not NumericFeatureManifest numeric)
            {
                continue;
            }

            double range = numeric.Max - numeric.Min;

            // Avoid division by zero or near-zero means.
            if (Math.Abs(numeric.Mean) < 1e-10 || range <= 0)
            {
                continue;
            }

            double rangeMeanRatio = range / Math.Abs(numeric.Mean);

            if (rangeMeanRatio <= thresholds.NormalizationMinRangeMeanRatio)
            {
                continue;
            }

            double confidence = Math.Min(1.0, 0.7 + (rangeMeanRatio / 100.0) * 0.3);

            EvidenceBuilder evidence = new EvidenceBuilder()
                .Add(numeric.Name, "min", numeric.Min)
                .Add(numeric.Name, "max", numeric.Max)
                .Add(numeric.Name, "mean", numeric.Mean)
                .Add(numeric.Name, "standardDeviation", numeric.StandardDeviation)
                .Add(numeric.Name, "range", range)
                .Add(numeric.Name, "rangeMeanRatio", rangeMeanRatio);

            yield return new RawFinding(
                InsightKind.NormalizationNeeded,
                InsightCategory.Scale,
                InsightSeverity.Info,
                confidence,
                InsightScope.Feature,
                $"Column '{numeric.Name}' has a range of {range:G4} (min={numeric.Min:G4}, max={numeric.Max:G4}) which is {rangeMeanRatio:F1}× the mean ({numeric.Mean:G4}).",
                "Features on vastly different scales cause gradient-based optimizers to oscillate and distance-based algorithms (KNN, SVM) to ignore smaller-scale features.",
                $"Standardize '{numeric.Name}' (zero mean, unit variance) or min-max normalize to [0, 1].",
                Rationale: null,
                Alternatives: ["Use robust scaling (IQR-based) if outliers are present.", "Use max-abs scaling if data is already centered."],
                [numeric.Name],
                [],
                ConflictGroup: null,
                evidence.Build());
        }
    }
}
