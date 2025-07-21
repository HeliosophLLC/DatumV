namespace DatumIngest.Manifest.Insights.Rules;

/// <summary>
/// Detects columns where the null ratio exceeds the high-missingness threshold.
/// Recommends imputation or investigation. At critical levels, flags as likely unusable.
/// </summary>
internal sealed class HighMissingnessRule : IInsightRule
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

            double nullRatio = feature.NullRatio.Value;
            bool isCritical = nullRatio > thresholds.CriticalMissingnessMinRatio;

            InsightKind kind = isCritical ? InsightKind.CriticalMissingness : InsightKind.HighMissingness;
            InsightSeverity severity = isCritical ? InsightSeverity.Critical : InsightSeverity.Warning;

            // Confidence increases with null ratio — at 80%+ we're very confident this is a problem.
            double confidence = Math.Min(1.0, 0.7 + (nullRatio * 0.3));

            EvidenceBuilder evidence = new EvidenceBuilder()
                .Add(feature.Name, "nullRatio", nullRatio)
                .Add(feature.Name, "nullCount", feature.NullCount)
                .Add(feature.Name, "validCount", feature.ValidCount);

            List<InsightAction> actions = new();

            if (isCritical)
            {
                actions.Add(new InsightAction(
                    ActionKind.Drop,
                    feature.Name,
                    Expression: null,
                    Alias: null,
                    Lossy: true,
                    Reversible: false,
                    BundleIdentifier: null));
            }

            string observation = isCritical
                ? $"Column '{feature.Name}' has {nullRatio:P1} missing values ({feature.NullCount:N0} of {feature.NullCount + feature.ValidCount:N0} rows)."
                : $"Column '{feature.Name}' has {nullRatio:P1} missing values ({feature.NullCount:N0} of {feature.NullCount + feature.ValidCount:N0} rows).";

            string risk = isCritical
                ? "At this missingness level, any imputation strategy introduces substantial noise. Models trained on this column will learn mostly from imputed values, not real data."
                : "Models will either drop these rows (losing substantial data) or require imputation, which may introduce bias if missingness is not completely at random.";

            string recommendation = isCritical
                ? $"Drop column '{feature.Name}' unless external data can fill the gaps. If the column is critical, investigate why data is missing before imputing."
                : $"Investigate missingness mechanism for '{feature.Name}'. If missing-at-random, impute with median (numeric) or mode (categorical). If informative, consider adding a missingness indicator.";

            yield return new RawFinding(
                kind,
                InsightCategory.DataQuality,
                severity,
                confidence,
                InsightScope.Feature,
                observation,
                risk,
                recommendation,
                Rationale: null,
                Alternatives: isCritical ? null : ["Drop the column if imputation quality is uncertain."],
                [feature.Name],
                actions,
                ConflictGroup: null,
                evidence.Build());
        }
    }
}
