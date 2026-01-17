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
            overlaid.Add(CloneFeature(feature, live, feature.CachedStatsValid));
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
    /// Returns a manifest where every feature has its
    /// <see cref="FeatureManifest.CachedStatsValid"/> flag forced to
    /// <paramref name="cachedStatsValid"/>. Used by the mutation-staleness
    /// signal (PR14j): a provider that observes a mutation flips this to
    /// <see langword="false"/> on the manifest it returns until ANALYZE
    /// runs and refreshes the cached half.
    /// </summary>
    public static QueryResultsManifest WithCachedStatsValid(
        QueryResultsManifest manifest, bool cachedStatsValid)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        bool anyChanges = false;
        FeatureManifest[] updated = new FeatureManifest[manifest.Features.Count];
        for (int i = 0; i < updated.Length; i++)
        {
            FeatureManifest feature = manifest.Features[i];
            if (feature.CachedStatsValid == cachedStatsValid)
            {
                updated[i] = feature;
            }
            else
            {
                updated[i] = CloneFeature(feature, live: null, cachedStatsValid);
                anyChanges = true;
            }
        }

        if (!anyChanges) return manifest;

        return new QueryResultsManifest
        {
            RowCount = manifest.RowCount,
            GeneratedAtUtc = manifest.GeneratedAtUtc,
            Features = updated,
            Interactions = manifest.Interactions,
            Insights = manifest.Insights,
            RecommendedQuery = manifest.RecommendedQuery,
            FullSuggestedQuery = manifest.FullSuggestedQuery,
            QueryAnnotations = manifest.QueryAnnotations,
            IndexHints = manifest.IndexHints,
        };
    }

    /// <summary>
    /// Builds a fresh subclass instance with the cached field set, swapping
    /// <paramref name="live"/>'s base-class fields when present and forcing
    /// <see cref="FeatureManifest.CachedStatsValid"/> to
    /// <paramref name="cachedStatsValid"/>.
    /// </summary>
    private static FeatureManifest CloneFeature(
        FeatureManifest cached, LiveColumnStats? live, bool cachedStatsValid)
    {
        long count = live?.Count ?? cached.Count;
        long nullCount = live?.NullCount ?? cached.NullCount;
        long distinct = live?.EstimatedDistinctCount ?? cached.EstimatedDistinctCount;
        double? nullRatio;
        if (live is not null)
        {
            long total = live.Count + live.NullCount;
            nullRatio = total > 0 ? (double?)live.NullCount / total : null;
        }
        else
        {
            nullRatio = cached.NullRatio;
        }

        return cached switch
        {
            NumericFeatureManifest n => new NumericFeatureManifest
            {
                Name = n.Name,
                Kind = n.Kind,
                IsArray = n.IsArray,
                Count = count,
                NullCount = nullCount,
                ValidCount = count,
                EstimatedDistinctCount = distinct,
                TopKValues = n.TopKValues,
                NullRatio = nullRatio,
                DominantValueRatio = n.DominantValueRatio,
                MissingRuns = n.MissingRuns,
                Entropy = n.Entropy,
                EntropyApproximate = n.EntropyApproximate,
                Role = n.Role,
                SchemaInference = n.SchemaInference,
                CachedStatsValid = cachedStatsValid,
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
                Count = count,
                NullCount = nullCount,
                ValidCount = count,
                EstimatedDistinctCount = distinct,
                TopKValues = s.TopKValues,
                NullRatio = nullRatio,
                DominantValueRatio = s.DominantValueRatio,
                MissingRuns = s.MissingRuns,
                Entropy = s.Entropy,
                EntropyApproximate = s.EntropyApproximate,
                Role = s.Role,
                SchemaInference = s.SchemaInference,
                CachedStatsValid = cachedStatsValid,
                MinLength = s.MinLength,
                MaxLength = s.MaxLength,
                CharacterClass = s.CharacterClass,
            },
            BooleanFeatureManifest b => new BooleanFeatureManifest
            {
                Name = b.Name,
                Kind = b.Kind,
                IsArray = b.IsArray,
                Count = count,
                NullCount = nullCount,
                ValidCount = count,
                EstimatedDistinctCount = distinct,
                TopKValues = b.TopKValues,
                NullRatio = nullRatio,
                DominantValueRatio = b.DominantValueRatio,
                MissingRuns = b.MissingRuns,
                Entropy = b.Entropy,
                EntropyApproximate = b.EntropyApproximate,
                Role = b.Role,
                SchemaInference = b.SchemaInference,
                CachedStatsValid = cachedStatsValid,
                TrueRatio = b.TrueRatio,
            },
            TemporalFeatureManifest t => new TemporalFeatureManifest
            {
                Name = t.Name,
                Kind = t.Kind,
                IsArray = t.IsArray,
                Count = count,
                NullCount = nullCount,
                ValidCount = count,
                EstimatedDistinctCount = distinct,
                TopKValues = t.TopKValues,
                NullRatio = nullRatio,
                DominantValueRatio = t.DominantValueRatio,
                MissingRuns = t.MissingRuns,
                Entropy = t.Entropy,
                EntropyApproximate = t.EntropyApproximate,
                Role = t.Role,
                SchemaInference = t.SchemaInference,
                CachedStatsValid = cachedStatsValid,
                Earliest = t.Earliest,
                Latest = t.Latest,
            },
            ArrayFeatureManifest a => new ArrayFeatureManifest
            {
                Name = a.Name,
                Kind = a.Kind,
                IsArray = a.IsArray,
                Count = count,
                NullCount = nullCount,
                ValidCount = count,
                EstimatedDistinctCount = distinct,
                TopKValues = a.TopKValues,
                NullRatio = nullRatio,
                DominantValueRatio = a.DominantValueRatio,
                MissingRuns = a.MissingRuns,
                Entropy = a.Entropy,
                EntropyApproximate = a.EntropyApproximate,
                Role = a.Role,
                SchemaInference = a.SchemaInference,
                CachedStatsValid = cachedStatsValid,
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
                Count = count,
                NullCount = nullCount,
                ValidCount = count,
                EstimatedDistinctCount = distinct,
                TopKValues = i.TopKValues,
                NullRatio = nullRatio,
                DominantValueRatio = i.DominantValueRatio,
                MissingRuns = i.MissingRuns,
                Entropy = i.Entropy,
                EntropyApproximate = i.EntropyApproximate,
                Role = i.Role,
                SchemaInference = i.SchemaInference,
                CachedStatsValid = cachedStatsValid,
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
            UuidFeatureManifest u => new UuidFeatureManifest
            {
                Name = u.Name,
                Kind = u.Kind,
                IsArray = u.IsArray,
                Count = count,
                NullCount = nullCount,
                ValidCount = count,
                EstimatedDistinctCount = distinct,
                TopKValues = u.TopKValues,
                NullRatio = nullRatio,
                DominantValueRatio = u.DominantValueRatio,
                MissingRuns = u.MissingRuns,
                Entropy = u.Entropy,
                EntropyApproximate = u.EntropyApproximate,
                Role = u.Role,
                SchemaInference = u.SchemaInference,
                CachedStatsValid = cachedStatsValid,
                VersionCounts = u.VersionCounts,
                EmbeddedTimestampEarliest = u.EmbeddedTimestampEarliest,
                EmbeddedTimestampLatest = u.EmbeddedTimestampLatest,
            },
            JsonFeatureManifest j => new JsonFeatureManifest
            {
                Name = j.Name,
                Kind = j.Kind,
                IsArray = j.IsArray,
                Count = count,
                NullCount = nullCount,
                ValidCount = count,
                EstimatedDistinctCount = distinct,
                TopKValues = j.TopKValues,
                NullRatio = nullRatio,
                DominantValueRatio = j.DominantValueRatio,
                MissingRuns = j.MissingRuns,
                Entropy = j.Entropy,
                EntropyApproximate = j.EntropyApproximate,
                Role = j.Role,
                SchemaInference = j.SchemaInference,
                CachedStatsValid = cachedStatsValid,
                RootTypeCounts = j.RootTypeCounts,
                TopLevelFieldCounts = j.TopLevelFieldCounts,
                MaxDepth = j.MaxDepth,
            },
            DecimalFeatureManifest dm => new DecimalFeatureManifest
            {
                Name = dm.Name,
                Kind = dm.Kind,
                IsArray = dm.IsArray,
                Count = count,
                NullCount = nullCount,
                ValidCount = count,
                EstimatedDistinctCount = distinct,
                TopKValues = dm.TopKValues,
                NullRatio = nullRatio,
                DominantValueRatio = dm.DominantValueRatio,
                MissingRuns = dm.MissingRuns,
                Entropy = dm.Entropy,
                EntropyApproximate = dm.EntropyApproximate,
                Role = dm.Role,
                SchemaInference = dm.SchemaInference,
                CachedStatsValid = cachedStatsValid,
                Min = dm.Min,
                Max = dm.Max,
                Mean = dm.Mean,
                Variance = dm.Variance,
                StandardDeviation = dm.StandardDeviation,
                ZeroCount = dm.ZeroCount,
                ZeroRatio = dm.ZeroRatio,
                IntegerValued = dm.IntegerValued,
            },
            BinaryFeatureManifest bin => new BinaryFeatureManifest
            {
                Name = bin.Name,
                Kind = bin.Kind,
                IsArray = bin.IsArray,
                Count = count,
                NullCount = nullCount,
                ValidCount = count,
                EstimatedDistinctCount = distinct,
                TopKValues = bin.TopKValues,
                NullRatio = nullRatio,
                DominantValueRatio = bin.DominantValueRatio,
                MissingRuns = bin.MissingRuns,
                Entropy = bin.Entropy,
                EntropyApproximate = bin.EntropyApproximate,
                Role = bin.Role,
                SchemaInference = bin.SchemaInference,
                CachedStatsValid = cachedStatsValid,
                SizeStats = bin.SizeStats,
            },
            // Unknown subclass: best-effort passthrough. The base class's
            // CachedStatsValid is `init`-only and can't be patched in place,
            // so callers that hit this arm get whatever flag was on the
            // input. Future subclasses must add an explicit case above.
            _ => cached,
        };
    }
}
