namespace DatumIngest.Manifest.Insights.Rules;

/// <summary>
/// Detects integer-valued numeric columns with a small number of distinct values,
/// suggesting ordinal encoding rather than treating as continuous.
/// </summary>
internal sealed class PossibleOrdinalRule : IInsightRule
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

            if (!numeric.IntegerValued ||
                numeric.EstimatedDistinctCount > thresholds.PossibleOrdinalMaxDistinct ||
                numeric.EstimatedDistinctCount <= 1)
            {
                continue;
            }

            EvidenceBuilder evidence = new EvidenceBuilder()
                .Add(numeric.Name, "estimatedDistinctCount", numeric.EstimatedDistinctCount)
                .Add(numeric.Name, "integerValued", numeric.IntegerValued)
                .Add(numeric.Name, "min", numeric.Min)
                .Add(numeric.Name, "max", numeric.Max);

            // Higher confidence when distinct count is very low.
            double confidence = Math.Min(1.0, 0.6 + (1.0 - (double)numeric.EstimatedDistinctCount / thresholds.PossibleOrdinalMaxDistinct) * 0.3);

            yield return new RawFinding(
                InsightKind.PossibleOrdinal,
                InsightCategory.Encoding,
                InsightSeverity.Info,
                confidence,
                InsightScope.Feature,
                $"Column '{numeric.Name}' is integer-valued with {numeric.EstimatedDistinctCount} distinct values (range [{numeric.Min:G4}, {numeric.Max:G4}]).",
                "Treating an ordinal feature as continuous imposes a linearity assumption on the level spacing, which may not reflect the true relationship with the target.",
                $"Verify whether '{numeric.Name}' represents ordered categories. If so, consider ordinal or one-hot encoding instead of raw numeric values.",
                Rationale: null,
                Alternatives: ["Keep as numeric if the integer spacing is meaningful (e.g., year, count).", "Use target encoding for high-cardinality ordinals."],
                [numeric.Name],
                [],
                ConflictGroup: null,
                evidence.Build());
        }
    }
}
