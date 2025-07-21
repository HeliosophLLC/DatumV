namespace DatumIngest.Manifest.Insights.Rules;

/// <summary>
/// Detects numeric columns where more than half the values are zero,
/// indicating a zero-inflated distribution that conflates structural zeros with the
/// underlying continuous process.
/// </summary>
internal sealed class ZeroInflatedRule : IInsightRule
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

            if (numeric.ZeroRatio <= thresholds.ZeroInflatedMinRatio)
            {
                continue;
            }

            string bundleIdentifier = $"zero-inflated-{numeric.Name}";
            string indicatorAlias = $"{numeric.Name}_nonzero";

            EvidenceBuilder evidence = new EvidenceBuilder()
                .Add(numeric.Name, "zeroRatio", numeric.ZeroRatio)
                .Add(numeric.Name, "zeroCount", numeric.ZeroCount)
                .Add(numeric.Name, "count", numeric.Count)
                .Add(numeric.Name, "mean", numeric.Mean)
                .Add(numeric.Name, "skewness", numeric.Skewness);

            if (numeric.NonzeroMean.HasValue)
            {
                evidence.Add(numeric.Name, "nonzeroMean", numeric.NonzeroMean.Value);
            }

            if (numeric.NonzeroVariance.HasValue)
            {
                evidence.Add(numeric.Name, "nonzeroVariance", numeric.NonzeroVariance.Value);
            }

            // Confidence scales with zero ratio — at 90%+ zeros, very confident.
            double confidence = Math.Min(1.0, 0.7 + (numeric.ZeroRatio * 0.3));

            List<InsightAction> actions =
            [
                new InsightAction(
                    ActionKind.Append,
                    Column: null,
                    Expression: $"CASE WHEN [{numeric.Name}] != 0 THEN 1 ELSE 0 END",
                    indicatorAlias,
                    Lossy: false,
                    Reversible: true,
                    bundleIdentifier),
            ];

            // If the nonzero subset has meaningful spread, suggest conditional log-transform.
            if (numeric.NonzeroMean.HasValue && numeric.Skewness > thresholds.RightSkewedMinSkewness)
            {
                actions.Add(new InsightAction(
                    ActionKind.Replace,
                    numeric.Name,
                    Expression: $"CASE WHEN [{numeric.Name}] > 0 THEN LOG([{numeric.Name}]) ELSE 0 END",
                    Alias: null,
                    Lossy: true,
                    Reversible: false,
                    bundleIdentifier));
            }

            yield return new RawFinding(
                InsightKind.ZeroInflated,
                InsightCategory.Distribution,
                InsightSeverity.Warning,
                confidence,
                InsightScope.Feature,
                $"Column '{numeric.Name}' has {numeric.ZeroRatio:P1} zero values ({numeric.ZeroCount:N0} of {numeric.Count:N0}). The overall mean is {numeric.Mean:G4} but the nonzero mean is {(numeric.NonzeroMean.HasValue ? numeric.NonzeroMean.Value.ToString("G4") : "N/A")}.",
                "Zero-inflated features conflate two distinct populations (zero vs. nonzero). Linear models and distance-based algorithms will be misled by the bimodal structure.",
                $"Split '{numeric.Name}' into a binary nonzero indicator ('{indicatorAlias}') and optionally log-transform the nonzero values.",
                Rationale: "Separating the zero/nonzero decision from the continuous magnitude lets models learn each pattern independently.",
                Alternatives: ["Use a zero-inflated regression model directly.", "Winsorize instead of log-transforming."],
                [numeric.Name],
                actions,
                ConflictGroup: null,
                evidence.Build());
        }
    }
}
