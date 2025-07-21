namespace DatumIngest.Manifest.Insights.Rules;

/// <summary>
/// Detects numeric columns with strong right or left skew beyond configured thresholds.
/// Recommends log-transform (right-skew) or reflection-then-log (left-skew).
/// </summary>
internal sealed class SkewnessRule : IInsightRule
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

            if (numeric.Skewness > thresholds.RightSkewedMinSkewness)
            {
                yield return EmitRightSkewed(numeric, thresholds);
            }
            else if (numeric.Skewness < thresholds.LeftSkewedMaxSkewness)
            {
                yield return EmitLeftSkewed(numeric, thresholds);
            }
        }
    }

    private static RawFinding EmitRightSkewed(NumericFeatureManifest numeric, InsightThresholds thresholds)
    {
        // Confidence scales with how far skewness exceeds the threshold.
        double excessSkew = numeric.Skewness - thresholds.RightSkewedMinSkewness;
        double confidence = Math.Min(1.0, 0.7 + (excessSkew * 0.05));

        EvidenceBuilder evidence = new EvidenceBuilder()
            .Add(numeric.Name, "skewness", numeric.Skewness)
            .Add(numeric.Name, "kurtosis", numeric.Kurtosis)
            .Add(numeric.Name, "mean", numeric.Mean)
            .Add(numeric.Name, "standardDeviation", numeric.StandardDeviation)
            .Add(numeric.Name, "min", numeric.Min)
            .Add(numeric.Name, "max", numeric.Max);

        bool allPositive = numeric.Min > 0;
        string expression = allPositive
            ? $"LOG([{numeric.Name}])"
            : $"LOG([{numeric.Name}] - {numeric.Min - 1:G6})";

        return new RawFinding(
            InsightKind.RightSkewed,
            InsightCategory.Distribution,
            InsightSeverity.Info,
            confidence,
            InsightScope.Feature,
            $"Column '{numeric.Name}' has skewness {numeric.Skewness:F2} (threshold: {thresholds.RightSkewedMinSkewness:F1}), indicating a right-skewed distribution.",
            "Right-skewed features compress most values into a narrow range while a long tail inflates variance. Distance-based and linear models underweight the bulk of the data.",
            $"Apply a log-transform to '{numeric.Name}' to reduce skewness.",
            Rationale: null,
            Alternatives: ["Apply a Box-Cox or Yeo-Johnson transform for optimal normalization.", "Use a rank-based transform if the log still leaves residual skew."],
            [numeric.Name],
            [new InsightAction(
                ActionKind.Replace,
                numeric.Name,
                expression,
                Alias: null,
                Lossy: true,
                Reversible: allPositive,
                BundleIdentifier: null)],
            ConflictGroup: null,
            evidence.Build());
    }

    private static RawFinding EmitLeftSkewed(NumericFeatureManifest numeric, InsightThresholds thresholds)
    {
        double excessSkew = Math.Abs(numeric.Skewness) - Math.Abs(thresholds.LeftSkewedMaxSkewness);
        double confidence = Math.Min(1.0, 0.7 + (excessSkew * 0.05));

        EvidenceBuilder evidence = new EvidenceBuilder()
            .Add(numeric.Name, "skewness", numeric.Skewness)
            .Add(numeric.Name, "kurtosis", numeric.Kurtosis)
            .Add(numeric.Name, "mean", numeric.Mean)
            .Add(numeric.Name, "min", numeric.Min)
            .Add(numeric.Name, "max", numeric.Max);

        // Reflect then log: LOG(max + 1 - x)
        string expression = $"LOG({numeric.Max + 1:G6} - [{numeric.Name}])";

        return new RawFinding(
            InsightKind.LeftSkewed,
            InsightCategory.Distribution,
            InsightSeverity.Info,
            confidence,
            InsightScope.Feature,
            $"Column '{numeric.Name}' has skewness {numeric.Skewness:F2} (threshold: {thresholds.LeftSkewedMaxSkewness:F1}), indicating a left-skewed distribution.",
            "Left-skewed features compress the upper range, causing models to underfit the dense upper region and overfit the sparse lower tail.",
            $"Apply a reflect-then-log transform to '{numeric.Name}'.",
            Rationale: "Reflection (max + 1 - x) converts left skew to right skew, then log compresses the tail.",
            Alternatives: ["Apply a Yeo-Johnson transform.", "Use quantile normalization."],
            [numeric.Name],
            [new InsightAction(
                ActionKind.Replace,
                numeric.Name,
                expression,
                Alias: null,
                Lossy: true,
                Reversible: true,
                BundleIdentifier: null)],
            ConflictGroup: null,
            evidence.Build());
    }
}
