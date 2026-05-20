namespace Heliosoph.DatumV.Manifest.Insights.Rules;

/// <summary>
/// Detects columns with zero variance (all non-null values identical).
/// A constant column carries no information and should be dropped.
/// </summary>
internal sealed class ConstantFeatureRule : IInsightRule
{
    /// <inheritdoc />
    public IEnumerable<RawFinding> Evaluate(QueryResultsManifest manifest, InsightThresholds thresholds)
    {
        foreach (FeatureManifest feature in manifest.Features)
        {
            if (!feature.IsConstant || feature.ValidCount == 0)
            {
                continue;
            }

            EvidenceBuilder evidence = new EvidenceBuilder()
                .Add(feature.Name, "estimatedDistinctCount", feature.EstimatedDistinctCount)
                .Add(feature.Name, "validCount", feature.ValidCount);

            if (feature is NumericFeatureManifest numeric)
            {
                evidence.Add(feature.Name, "variance", numeric.Variance);
            }

            if (feature.TopKValues.Count > 0)
            {
                evidence.Add(feature.Name, "constantValue", feature.TopKValues[0].Value);
            }

            // Constant feature is deterministic — confidence is essentially 1.0.
            // Slight discount if HyperLogLog could be off.
            double confidence = feature.ValidCount >= 100 ? 0.99 : 0.95;

            yield return new RawFinding(
                InsightKind.ConstantFeature,
                InsightCategory.DataQuality,
                InsightSeverity.Warning,
                confidence,
                InsightScope.Feature,
                $"Column '{feature.Name}' has a single distinct value across all {feature.ValidCount:N0} valid rows.",
                "A constant column has zero information gain. Including it wastes compute and can cause numerical issues in some algorithms (e.g., division by std dev).",
                $"Drop column '{feature.Name}'.",
                Rationale: null,
                Alternatives: null,
                [feature.Name],
                [new InsightAction(
                    ActionKind.Drop,
                    feature.Name,
                    Expression: null,
                    Alias: null,
                    Lossy: false,
                    Reversible: true,
                    BundleIdentifier: null)],
                ConflictGroup: null,
                evidence.Build());
        }
    }
}
