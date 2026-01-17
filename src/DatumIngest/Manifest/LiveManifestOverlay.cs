namespace DatumIngest.Manifest;

using DatumIngest.Indexing;

/// <summary>
/// Composes a fresh <see cref="QueryResultsManifest"/> by overlaying live column
/// statistics derived from a <see cref="SourceIndex"/> onto a cached
/// <see cref="QueryResultsManifest"/> loaded from <c>.datum-manifest</c>.
/// </summary>
/// <remarks>
/// <para>
/// The hybrid manifest design splits each <see cref="FeatureManifest"/> into a
/// "live" half (count, null ratio, distinct estimate, computed from per-chunk
/// index statistics) and a "cached" half (top-K, quantiles, histogram, entropy,
/// kind-specific summaries — refreshed by <c>ANALYZE</c>). On every snapshot
/// rebuild the provider calls <see cref="Compose"/> once to fold the live half
/// into the per-column manifest, so reads are O(1) thereafter.
/// </para>
/// <para>
/// Cached fields are preserved verbatim on the cloned subclass instance. Only
/// the live base-class fields (<see cref="FeatureManifest.Count"/>,
/// <see cref="FeatureManifest.NullCount"/>, <see cref="FeatureManifest.ValidCount"/>,
/// <see cref="FeatureManifest.NullRatio"/>,
/// <see cref="FeatureManifest.EstimatedDistinctCount"/>) are overwritten; subclass
/// fields like <c>NumericFeatureManifest.Min</c> / <c>Max</c> / <c>Mean</c> stay
/// on the cached path until the kind-specific overlay arms ship.
/// </para>
/// </remarks>
public static class LiveManifestOverlay
{
    /// <summary>
    /// Returns a new <see cref="QueryResultsManifest"/> where each
    /// <see cref="FeatureManifest"/> has its live half replaced with values
    /// computed from <paramref name="index"/>. Columns absent from the index
    /// (no per-chunk statistics) pass through unchanged. The returned manifest
    /// uses the live <see cref="FeatureManifest.Count"/> totals to recompute
    /// <see cref="QueryResultsManifest.RowCount"/>; if the cached manifest had
    /// no live-eligible columns the row count is preserved.
    /// </summary>
    public static QueryResultsManifest Compose(QueryResultsManifest cached, SourceIndex index)
    {
        ArgumentNullException.ThrowIfNull(cached);
        ArgumentNullException.ThrowIfNull(index);

        List<FeatureManifest> overlaid = new(cached.Features.Count);
        long? freshRowCount = null;

        foreach (FeatureManifest feature in cached.Features)
        {
            LiveColumnStats? live = LiveColumnStats.ComputeFromIndex(index, feature.Name);
            if (live is null)
            {
                overlaid.Add(feature);
                continue;
            }

            // First column with a live stat establishes the table-level row
            // count. Every column should agree, so we don't sum across them —
            // taking the first is enough.
            freshRowCount ??= live.Count + live.NullCount;
            overlaid.Add(CloneWithLiveStats(feature, live));
        }

        return new QueryResultsManifest
        {
            RowCount = freshRowCount ?? cached.RowCount,
            GeneratedAtUtc = cached.GeneratedAtUtc,
            Features = overlaid,
            Interactions = cached.Interactions,
            Insights = cached.Insights,
            RecommendedQuery = cached.RecommendedQuery,
            FullSuggestedQuery = cached.FullSuggestedQuery,
            QueryAnnotations = cached.QueryAnnotations,
            IndexHints = cached.IndexHints,
        };
    }

    /// <summary>
    /// Builds a fresh subclass instance with the cached field set, but with
    /// <paramref name="live"/>'s base-class fields swapped in. The
    /// <see cref="FeatureManifest.CachedStatsValid"/> flag is preserved from
    /// the cached input — overlay says nothing about whether the cached half is
    /// fresh; that's owned by the mutation-staleness signal (PR14j).
    /// </summary>
    private static FeatureManifest CloneWithLiveStats(FeatureManifest cached, LiveColumnStats live)
    {
        long total = live.Count + live.NullCount;
        double? nullRatio = total > 0 ? (double?)live.NullCount / total : null;

        return cached switch
        {
            NumericFeatureManifest n => new NumericFeatureManifest
            {
                Name = n.Name,
                Kind = n.Kind,
                IsArray = n.IsArray,
                Count = live.Count,
                NullCount = live.NullCount,
                ValidCount = live.Count,
                EstimatedDistinctCount = live.EstimatedDistinctCount,
                TopKValues = n.TopKValues,
                NullRatio = nullRatio,
                DominantValueRatio = n.DominantValueRatio,
                MissingRuns = n.MissingRuns,
                Entropy = n.Entropy,
                EntropyApproximate = n.EntropyApproximate,
                Role = n.Role,
                SchemaInference = n.SchemaInference,
                CachedStatsValid = n.CachedStatsValid,
                Min = n.Min,
                Max = n.Max,
                Mean = n.Mean,
                Variance = n.Variance,
                StandardDeviation = n.StandardDeviation,
                Skewness = n.Skewness,
                Kurtosis = n.Kurtosis,
                Histogram = n.Histogram,
                Quantiles = n.Quantiles,
                ZeroCount = n.ZeroCount,
                ZeroRatio = n.ZeroRatio,
                OutlierCount = n.OutlierCount,
                OutlierRatio = n.OutlierRatio,
                IntegerValued = n.IntegerValued,
                NonzeroCount = n.NonzeroCount,
                NonzeroMean = n.NonzeroMean,
                NonzeroVariance = n.NonzeroVariance,
                NonzeroStandardDeviation = n.NonzeroStandardDeviation,
            },
            StringFeatureManifest s => new StringFeatureManifest
            {
                Name = s.Name,
                Kind = s.Kind,
                IsArray = s.IsArray,
                Count = live.Count,
                NullCount = live.NullCount,
                ValidCount = live.Count,
                EstimatedDistinctCount = live.EstimatedDistinctCount,
                TopKValues = s.TopKValues,
                NullRatio = nullRatio,
                DominantValueRatio = s.DominantValueRatio,
                MissingRuns = s.MissingRuns,
                Entropy = s.Entropy,
                EntropyApproximate = s.EntropyApproximate,
                Role = s.Role,
                SchemaInference = s.SchemaInference,
                CachedStatsValid = s.CachedStatsValid,
                MinLength = s.MinLength,
                MaxLength = s.MaxLength,
                CharacterClass = s.CharacterClass,
            },
            BooleanFeatureManifest b => new BooleanFeatureManifest
            {
                Name = b.Name,
                Kind = b.Kind,
                IsArray = b.IsArray,
                Count = live.Count,
                NullCount = live.NullCount,
                ValidCount = live.Count,
                EstimatedDistinctCount = live.EstimatedDistinctCount,
                TopKValues = b.TopKValues,
                NullRatio = nullRatio,
                DominantValueRatio = b.DominantValueRatio,
                MissingRuns = b.MissingRuns,
                Entropy = b.Entropy,
                EntropyApproximate = b.EntropyApproximate,
                Role = b.Role,
                SchemaInference = b.SchemaInference,
                CachedStatsValid = b.CachedStatsValid,
                TrueRatio = b.TrueRatio,
            },
            TemporalFeatureManifest t => new TemporalFeatureManifest
            {
                Name = t.Name,
                Kind = t.Kind,
                IsArray = t.IsArray,
                Count = live.Count,
                NullCount = live.NullCount,
                ValidCount = live.Count,
                EstimatedDistinctCount = live.EstimatedDistinctCount,
                TopKValues = t.TopKValues,
                NullRatio = nullRatio,
                DominantValueRatio = t.DominantValueRatio,
                MissingRuns = t.MissingRuns,
                Entropy = t.Entropy,
                EntropyApproximate = t.EntropyApproximate,
                Role = t.Role,
                SchemaInference = t.SchemaInference,
                CachedStatsValid = t.CachedStatsValid,
                Earliest = t.Earliest,
                Latest = t.Latest,
            },
            ArrayFeatureManifest a => new ArrayFeatureManifest
            {
                Name = a.Name,
                Kind = a.Kind,
                IsArray = a.IsArray,
                Count = live.Count,
                NullCount = live.NullCount,
                ValidCount = live.Count,
                EstimatedDistinctCount = live.EstimatedDistinctCount,
                TopKValues = a.TopKValues,
                NullRatio = nullRatio,
                DominantValueRatio = a.DominantValueRatio,
                MissingRuns = a.MissingRuns,
                Entropy = a.Entropy,
                EntropyApproximate = a.EntropyApproximate,
                Role = a.Role,
                SchemaInference = a.SchemaInference,
                CachedStatsValid = a.CachedStatsValid,
                MinLength = a.MinLength,
                MaxLength = a.MaxLength,
                ElementStats = a.ElementStats,
                ZeroElementCount = a.ZeroElementCount,
                ZeroElementRatio = a.ZeroElementRatio,
                ZeroArrayCount = a.ZeroArrayCount,
                NormMin = a.NormMin,
                NormMax = a.NormMax,
                NormMean = a.NormMean,
            },
            ImageFeatureManifest i => new ImageFeatureManifest
            {
                Name = i.Name,
                Kind = i.Kind,
                IsArray = i.IsArray,
                Count = live.Count,
                NullCount = live.NullCount,
                ValidCount = live.Count,
                EstimatedDistinctCount = live.EstimatedDistinctCount,
                TopKValues = i.TopKValues,
                NullRatio = nullRatio,
                DominantValueRatio = i.DominantValueRatio,
                MissingRuns = i.MissingRuns,
                Entropy = i.Entropy,
                EntropyApproximate = i.EntropyApproximate,
                Role = i.Role,
                SchemaInference = i.SchemaInference,
                CachedStatsValid = i.CachedStatsValid,
                MinWidth = i.MinWidth,
                MaxWidth = i.MaxWidth,
                MinHeight = i.MinHeight,
                MaxHeight = i.MaxHeight,
                ChannelCounts = i.ChannelCounts,
                OrientationCounts = i.OrientationCounts,
                UndecodableCount = i.UndecodableCount,
                TinyImageCount = i.TinyImageCount,
                HugeImageCount = i.HugeImageCount,
                FileSizeStats = i.FileSizeStats,
                MegapixelStats = i.MegapixelStats,
                PixelCountStats = i.PixelCountStats,
                AspectRatioStats = i.AspectRatioStats,
                AspectRatioHistogram = i.AspectRatioHistogram,
            },
            BinaryFeatureManifest bin => new BinaryFeatureManifest
            {
                Name = bin.Name,
                Kind = bin.Kind,
                IsArray = bin.IsArray,
                Count = live.Count,
                NullCount = live.NullCount,
                ValidCount = live.Count,
                EstimatedDistinctCount = live.EstimatedDistinctCount,
                TopKValues = bin.TopKValues,
                NullRatio = nullRatio,
                DominantValueRatio = bin.DominantValueRatio,
                MissingRuns = bin.MissingRuns,
                Entropy = bin.Entropy,
                EntropyApproximate = bin.EntropyApproximate,
                Role = bin.Role,
                SchemaInference = bin.SchemaInference,
                CachedStatsValid = bin.CachedStatsValid,
                SizeStats = bin.SizeStats,
            },
            _ => cached,
        };
    }
}
