namespace DatumIngest.Manifest;

/// <summary>
/// Derives advisory suggestion tags from a <see cref="FeatureManifest"/> using
/// configurable <see cref="SuggestionThresholds"/>. Suggestions are heuristic
/// labels that highlight notable statistical patterns — consumers should treat
/// them as hints, not definitive classifications.
/// </summary>
public static class SuggestionEngine
{
    /// <summary>
    /// Computes suggestion tags for the given feature manifest. Returns null
    /// when no suggestions apply (avoids serializing an empty array).
    /// </summary>
    /// <param name="feature">The feature manifest to analyze.</param>
    /// <param name="rowCount">Total row count for the dataset, used for ratio calculations.</param>
    /// <param name="thresholds">Thresholds controlling when each suggestion fires.</param>
    public static IReadOnlyList<string>? Suggest(
        FeatureManifest feature,
        long rowCount,
        SuggestionThresholds thresholds)
    {
        List<string> suggestions = new();

        ApplyUniversalSuggestions(feature, rowCount, thresholds, suggestions);

        if (feature is NumericFeatureManifest numeric)
        {
            ApplyNumericSuggestions(numeric, thresholds, suggestions);
        }

        return suggestions.Count > 0 ? suggestions : null;
    }

    /// <summary>
    /// Applies suggestions that are valid for any feature type.
    /// </summary>
    private static void ApplyUniversalSuggestions(
        FeatureManifest feature,
        long rowCount,
        SuggestionThresholds thresholds,
        List<string> suggestions)
    {
        // High missingness: large proportion of null/empty values.
        if (feature.NullRatio.HasValue && feature.NullRatio.Value > thresholds.HighMissingnessMinRatio)
        {
            suggestions.Add("high-missingness");
        }

        // Low cardinality: very few distinct values.
        if (feature.EstimatedDistinctCount <= thresholds.LowCardinalityMaxDistinct)
        {
            suggestions.Add("low-cardinality");
        }

        // High cardinality: most values are unique.
        if (rowCount > 0)
        {
            double distinctRatio = (double)feature.EstimatedDistinctCount / rowCount;

            if (distinctRatio > thresholds.HighCardinalityMinDistinctRatio)
            {
                suggestions.Add("high-cardinality");
            }

            // Possible identifier: nearly all values unique and no dominant value.
            if (distinctRatio > thresholds.PossibleIdentifierMinDistinctRatio)
            {
                double topKCoverageRatio = ComputeTopKCoverageRatio(feature.TopKValues, rowCount);

                if (topKCoverageRatio < thresholds.PossibleIdentifierMaxTopKCoverage)
                {
                    suggestions.Add("possible-identifier");
                }
            }
        }
    }

    /// <summary>
    /// Applies suggestions specific to numeric columns.
    /// </summary>
    private static void ApplyNumericSuggestions(
        NumericFeatureManifest numeric,
        SuggestionThresholds thresholds,
        List<string> suggestions)
    {
        // Zero-inflated: more than half the values are zero.
        if (numeric.ZeroRatio > thresholds.ZeroInflatedMinRatio)
        {
            suggestions.Add("zero-inflated");
        }

        // Possible ordinal: integer-valued with a small number of distinct levels.
        if (numeric.IntegerValued &&
            numeric.EstimatedDistinctCount <= thresholds.PossibleOrdinalMaxDistinct)
        {
            suggestions.Add("possible-ordinal");
        }

        // Right-skewed: strong positive skew.
        if (numeric.Skewness > thresholds.RightSkewedMinSkewness)
        {
            suggestions.Add("right-skewed");
        }

        // Left-skewed: strong negative skew.
        if (numeric.Skewness < thresholds.LeftSkewedMaxSkewness)
        {
            suggestions.Add("left-skewed");
        }

        // Heavy-tailed: excess kurtosis indicates outlier-prone distribution.
        if (numeric.Kurtosis > thresholds.HeavyTailedMinKurtosis)
        {
            suggestions.Add("heavy-tailed");
        }
    }

    /// <summary>
    /// Computes the fraction of all rows covered by the top-K most frequent values.
    /// </summary>
    private static double ComputeTopKCoverageRatio(IReadOnlyList<FrequencyEntry> topKValues, long rowCount)
    {
        if (rowCount == 0 || topKValues.Count == 0)
        {
            return 0.0;
        }

        long totalFrequency = 0;

        foreach (FrequencyEntry entry in topKValues)
        {
            totalFrequency += entry.Frequency;
        }

        return (double)totalFrequency / rowCount;
    }
}
