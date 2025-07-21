namespace DatumIngest.Manifest.Insights.Rules;

/// <summary>
/// Detects numeric columns with excess kurtosis above the threshold, indicating
/// heavy tails that produce more extreme outliers than a normal distribution.
/// </summary>
internal sealed class HeavyTailedRule : IInsightRule
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

            if (numeric.Kurtosis <= thresholds.HeavyTailedMinKurtosis)
            {
                continue;
            }

            double excessKurtosis = numeric.Kurtosis - thresholds.HeavyTailedMinKurtosis;
            double confidence = Math.Min(1.0, 0.65 + (excessKurtosis * 0.02));

            EvidenceBuilder evidence = new EvidenceBuilder()
                .Add(numeric.Name, "kurtosis", numeric.Kurtosis)
                .Add(numeric.Name, "skewness", numeric.Skewness)
                .Add(numeric.Name, "outlierRatio", numeric.OutlierRatio)
                .Add(numeric.Name, "outlierCount", numeric.OutlierCount)
                .Add(numeric.Name, "min", numeric.Min)
                .Add(numeric.Name, "max", numeric.Max);

            if (numeric.Quantiles is not null)
            {
                evidence
                    .Add(numeric.Name, "p01", numeric.Quantiles.P01)
                    .Add(numeric.Name, "p99", numeric.Quantiles.P99);
            }

            yield return new RawFinding(
                InsightKind.HeavyTailed,
                InsightCategory.Distribution,
                InsightSeverity.Info,
                confidence,
                InsightScope.Feature,
                $"Column '{numeric.Name}' has kurtosis {numeric.Kurtosis:F2} (threshold: {thresholds.HeavyTailedMinKurtosis:F1}), indicating heavy tails with more extreme values than a normal distribution.",
                "Heavy-tailed features produce outliers that inflate gradient magnitudes and loss values, slowing convergence and destabilizing training.",
                $"Clip '{numeric.Name}' at the 1st and 99th percentiles, or apply a robust scaler.",
                Rationale: null,
                Alternatives: ["Apply Winsorization to cap extreme values.", "Use a rank-based encoding."],
                [numeric.Name],
                [],
                ConflictGroup: null,
                evidence.Build());
        }
    }
}
