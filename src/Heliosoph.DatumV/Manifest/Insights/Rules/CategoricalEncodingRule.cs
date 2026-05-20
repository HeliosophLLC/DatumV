namespace Heliosoph.DatumV.Manifest.Insights.Rules;

/// <summary>
/// Detects string columns with low or high cardinality and recommends
/// appropriate encoding strategies. Also detects binary features (exactly 2 distinct values).
/// </summary>
internal sealed class CategoricalEncodingRule : IInsightRule
{
    /// <inheritdoc />
    public IEnumerable<RawFinding> Evaluate(QueryResultsManifest manifest, InsightThresholds thresholds)
    {
        foreach (FeatureManifest feature in manifest.Features)
        {
            if (feature is not StringFeatureManifest)
            {
                continue;
            }

            if (feature.ValidCount == 0)
            {
                continue;
            }

            long distinctCount = feature.EstimatedDistinctCount;

            if (distinctCount == 2)
            {
                yield return EmitBinaryFeature(feature);
            }
            else if (distinctCount <= thresholds.OneHotMaxDistinct && distinctCount > 1)
            {
                yield return EmitLowCardinality(feature, thresholds);
            }
            else if (distinctCount > thresholds.OneHotMaxDistinct)
            {
                yield return EmitHighCardinality(feature, thresholds, manifest.RowCount);
            }
        }
    }

    private static RawFinding EmitBinaryFeature(FeatureManifest feature)
    {
        EvidenceBuilder evidence = new EvidenceBuilder()
            .Add(feature.Name, "estimatedDistinctCount", feature.EstimatedDistinctCount);

        if (feature.TopKValues.Count >= 2)
        {
            evidence
                .Add(feature.Name, "value0", feature.TopKValues[0].Value)
                .Add(feature.Name, "value1", feature.TopKValues[1].Value)
                .Add(feature.Name, "frequency0", feature.TopKValues[0].Frequency)
                .Add(feature.Name, "frequency1", feature.TopKValues[1].Frequency);
        }

        return new RawFinding(
            InsightKind.BinaryFeature,
            InsightCategory.Encoding,
            InsightSeverity.Info,
            0.95,
            InsightScope.Feature,
            $"Column '{feature.Name}' has exactly 2 distinct values.",
            "Binary columns should be encoded as 0/1 for most ML algorithms. String representations waste memory and prevent numeric operations.",
            $"Encode '{feature.Name}' as a binary 0/1 column.",
            Rationale: null,
            Alternatives: null,
            [feature.Name],
            [],
            ConflictGroup: null,
            evidence.Build());
    }

    private static RawFinding EmitLowCardinality(FeatureManifest feature, InsightThresholds thresholds)
    {
        EvidenceBuilder evidence = new EvidenceBuilder()
            .Add(feature.Name, "estimatedDistinctCount", feature.EstimatedDistinctCount);

        return new RawFinding(
            InsightKind.LowCardinalityCategorical,
            InsightCategory.Encoding,
            InsightSeverity.Info,
            0.85,
            InsightScope.Feature,
            $"Column '{feature.Name}' has {feature.EstimatedDistinctCount} distinct values (≤ {thresholds.OneHotMaxDistinct} threshold).",
            "Low-cardinality categoricals are ideal for one-hot encoding, which preserves all category information without imposing ordinal relationships.",
            $"Apply one-hot encoding to '{feature.Name}'.",
            Rationale: null,
            Alternatives: ["Use label encoding if tree-based models are the target.", "Use target encoding if there is a clear target variable."],
            [feature.Name],
            [],
            ConflictGroup: null,
            evidence.Build());
    }

    private static RawFinding EmitHighCardinality(FeatureManifest feature, InsightThresholds thresholds, long rowCount)
    {
        EvidenceBuilder evidence = new EvidenceBuilder()
            .Add(feature.Name, "estimatedDistinctCount", feature.EstimatedDistinctCount);

        if (rowCount > 0)
        {
            evidence.Add(feature.Name, "distinctRatio", (double)feature.EstimatedDistinctCount / rowCount);
        }

        return new RawFinding(
            InsightKind.HighCardinalityCategorical,
            InsightCategory.Encoding,
            InsightSeverity.Warning,
            0.80,
            InsightScope.Feature,
            $"Column '{feature.Name}' has {feature.EstimatedDistinctCount} distinct values (> {thresholds.OneHotMaxDistinct} threshold).",
            $"One-hot encoding this column would create {feature.EstimatedDistinctCount} sparse columns, inflating dimensionality and slowing training. Most categories will have too few examples for reliable learning.",
            $"Use target encoding, hashing, or embedding for '{feature.Name}'. If many categories are rare, consider grouping infrequent values.",
            Rationale: null,
            Alternatives: ["Drop the column if it does not carry predictive signal.", "Use frequency encoding."],
            [feature.Name],
            [],
            ConflictGroup: null,
            evidence.Build());
    }
}
