namespace Heliosoph.DatumV.Manifest.Insights.Rules;

/// <summary>
/// Detects numeric columns where the Z-score outlier ratio exceeds the threshold.
/// </summary>
internal sealed class ExtremeOutliersRule : IInsightRule
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

            if (numeric.OutlierRatio <= thresholds.ExtremeOutlierMinRatio)
            {
                continue;
            }

            double confidence = Math.Min(1.0, 0.7 + (numeric.OutlierRatio * 2.0));

            EvidenceBuilder evidence = new EvidenceBuilder()
                .Add(numeric.Name, "outlierRatio", numeric.OutlierRatio)
                .Add(numeric.Name, "outlierCount", numeric.OutlierCount)
                .Add(numeric.Name, "count", numeric.Count)
                .Add(numeric.Name, "mean", numeric.Mean)
                .Add(numeric.Name, "standardDeviation", numeric.StandardDeviation)
                .Add(numeric.Name, "min", numeric.Min)
                .Add(numeric.Name, "max", numeric.Max);

            if (numeric.Quantiles is not null)
            {
                evidence
                    .Add(numeric.Name, "p01", numeric.Quantiles.P01)
                    .Add(numeric.Name, "p99", numeric.Quantiles.P99)
                    .Add(numeric.Name, "lowerFence", numeric.Quantiles.LowerFence)
                    .Add(numeric.Name, "upperFence", numeric.Quantiles.UpperFence);
            }

            string clipExpression = numeric.Quantiles is not null
                ? $"CLIP([{numeric.Name}], {numeric.Quantiles.P01:G6}, {numeric.Quantiles.P99:G6})"
                : $"CLIP([{numeric.Name}], {numeric.Mean - 3 * numeric.StandardDeviation:G6}, {numeric.Mean + 3 * numeric.StandardDeviation:G6})";

            yield return new RawFinding(
                InsightKind.ExtremeOutliers,
                InsightCategory.Distribution,
                InsightSeverity.Warning,
                confidence,
                InsightScope.Feature,
                $"Column '{numeric.Name}' has {numeric.OutlierRatio:P1} outliers ({numeric.OutlierCount:N0} of {numeric.Count:N0} values exceed 3 standard deviations from the mean).",
                "Extreme outliers dominate loss functions (especially MSE), bias gradient updates, and inflate variance estimates used by standardization.",
                $"Clip '{numeric.Name}' to the 1st–99th percentile range to limit outlier influence.",
                Rationale: null,
                Alternatives: ["Winsorize at the 5th and 95th percentiles.", "Apply a log-transform if the distribution is also skewed.", "Use a robust scaler (IQR-based)."],
                [numeric.Name],
                [new InsightAction(
                    ActionKind.Replace,
                    numeric.Name,
                    clipExpression,
                    Alias: null,
                    Lossy: true,
                    Reversible: false,
                    BundleIdentifier: null)],
                ConflictGroup: null,
                evidence.Build());
        }
    }
}
