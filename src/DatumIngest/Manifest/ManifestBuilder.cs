namespace DatumIngest.Manifest;

using DatumIngest.Indexing;
using DatumIngest.Manifest.Insights;
using DatumIngest.Model;
using DatumIngest.Statistics;
using DatumIngest.Statistics.Accumulators;
using DatumIngest.Statistics.Interactions;

/// <summary>
/// Builds a <see cref="QueryResultsManifest"/> from collected column statistics,
/// mapping each column to the appropriate <see cref="FeatureManifest"/> subclass
/// based on its <see cref="DataKind"/>.
/// </summary>
public static class ManifestBuilder
{
    /// <summary>
    /// Builds a manifest from the given column statistics and schema metadata.
    /// </summary>
    /// <param name="statistics">Per-column statistics from <see cref="StatisticsCollector"/>.</param>
    /// <param name="columnKinds">Map of column name to <see cref="DataKind"/>, used to select the correct manifest subclass.</param>
    /// <param name="rowCount">Total number of rows in the result set.</param>
    /// <param name="interactions">Optional pairwise column interaction results.</param>
    /// <param name="insightThresholds">Optional thresholds for the insight analysis engine. Pass null to disable insights.</param>
    public static QueryResultsManifest Build(
        IReadOnlyDictionary<string, ColumnStatistics> statistics,
        IReadOnlyDictionary<string, DataKind> columnKinds,
        long rowCount,
        IReadOnlyList<ColumnInteractionResult>? interactions = null,
        InsightThresholds? insightThresholds = null)
    {
        List<FeatureManifest> features = new();

        foreach (KeyValuePair<string, ColumnStatistics> entry in statistics)
        {
            DataKind kind = columnKinds.TryGetValue(entry.Key, out DataKind k) ? k : DataKind.String;
            FeatureManifest featureManifest = BuildFeature(entry.Key, kind, entry.Value, rowCount);
            features.Add(featureManifest);
        }

        List<ColumnInteraction>? mappedInteractions = null;

        if (interactions is { Count: > 0 })
        {
            mappedInteractions = new List<ColumnInteraction>(interactions.Count);

            foreach (ColumnInteractionResult result in interactions)
            {
                mappedInteractions.Add(new ColumnInteraction
                {
                    ColumnA = result.ColumnA,
                    ColumnB = result.ColumnB,
                    Pearson = result.Pearson,
                    Spearman = result.Spearman,
                    CramerV = result.CramerV,
                    AnovaFStatistic = result.AnovaFStatistic,
                    MutualInformation = result.MutualInformation,
                    TheilUAB = result.TheilUAB,
                    TheilUBA = result.TheilUBA,
                    MissingnessCorrelation = result.MissingnessCorrelation
                });
            }
        }

        QueryResultsManifest manifest = new()
        {
            RowCount = rowCount,
            GeneratedAtUtc = DateTime.UtcNow,
            Features = features,
            Interactions = mappedInteractions,
            IndexHints = GenerateIndexHints(features, columnKinds)
        };

        if (insightThresholds is not null)
        {
            IReadOnlyList<DatasetInsight> insights = InsightAnalyzer.Analyze(manifest, insightThresholds);

            List<string> originalColumns = new(features.Count);

            foreach (FeatureManifest feature in features)
            {
                originalColumns.Add(feature.Name);
            }

            QuerySynthesisOptions synthesisOptions = new();
            IReadOnlyList<QueryAnnotation> annotations = QuerySynthesizer.GenerateAnnotations(insights);

            manifest = new QueryResultsManifest
            {
                RowCount = manifest.RowCount,
                GeneratedAtUtc = manifest.GeneratedAtUtc,
                Features = manifest.Features,
                Interactions = manifest.Interactions,
                IndexHints = manifest.IndexHints,
                Insights = insights.Count > 0 ? insights : null,
                RecommendedQuery = QuerySynthesizer.SynthesizeRecommended(insights, originalColumns, synthesisOptions),
                FullSuggestedQuery = QuerySynthesizer.SynthesizeFull(insights, originalColumns, synthesisOptions),
                QueryAnnotations = annotations.Count > 0 ? annotations : null
            };
        }

        return manifest;
    }

    private static FeatureManifest BuildFeature(string name, DataKind kind, ColumnStatistics stats, long rowCount)
    {
        // Extract common fields
        CountResult? countResult = GetResultValue<CountResult>(stats, "count");
        CardinalityResult? cardinalityResult = GetResultValue<CardinalityResult>(stats, "cardinality");
        TopKResult? topKResult = GetResultValue<TopKResult>(stats, "top_k");
        MissingRunsResult? missingRunsResult = GetResultValue<MissingRunsResult>(stats, "missing_runs");

        long count = countResult?.NonNull ?? 0;
        long nullCount = countResult?.NullOrEmpty ?? 0;
        long distinctCount = cardinalityResult?.EstimatedDistinctCount ?? 0;
        double? nullRatio = rowCount > 0 ? (double)nullCount / rowCount : null;
        long? missingRuns = missingRunsResult?.RunCount;
        IReadOnlyList<FrequencyEntry> topK = MapTopK(topKResult);
        double? dominantValueRatio = rowCount > 0 && topK.Count > 0 ? (double)topK[0].Frequency / rowCount : null;
        EntropyResult? entropyResult = GetResultValue<EntropyResult>(stats, "entropy");

        return kind switch
        {
            DataKind.Scalar or DataKind.UInt8 => BuildNumericManifest(name, kind, count, nullCount, nullRatio, dominantValueRatio, missingRuns, distinctCount, topK, entropyResult, stats),
            DataKind.String or DataKind.JsonValue => BuildStringManifest(name, kind, count, nullCount, nullRatio, dominantValueRatio, missingRuns, distinctCount, topK, entropyResult, stats),
            DataKind.Vector => BuildVectorManifest(name, kind, count, nullCount, nullRatio, dominantValueRatio, missingRuns, distinctCount, topK, stats),
            DataKind.Matrix or DataKind.Tensor => BuildTensorManifest(name, kind, count, nullCount, nullRatio, dominantValueRatio, missingRuns, distinctCount, topK, stats),
            DataKind.Image => BuildImageManifest(name, kind, count, nullCount, nullRatio, dominantValueRatio, missingRuns, distinctCount, topK, stats),
            DataKind.UInt8Array => BuildBinaryManifest(name, kind, count, nullCount, nullRatio, dominantValueRatio, missingRuns, distinctCount, topK, stats),
            DataKind.Date or DataKind.DateTime => BuildTemporalManifest(name, kind, count, nullCount, nullRatio, dominantValueRatio, missingRuns, distinctCount, topK, entropyResult, stats),
            _ => BuildFallbackManifest(name, kind, count, nullCount, nullRatio, dominantValueRatio, missingRuns, distinctCount, topK)
        };
    }

    private static NumericFeatureManifest BuildNumericManifest(
        string name, DataKind kind, long count, long nullCount, double? nullRatio, double? dominantValueRatio, long? missingRuns, long distinctCount,
        IReadOnlyList<FrequencyEntry> topK, EntropyResult? entropyResult, ColumnStatistics stats)
    {
        NumericResult numericResult = GetResultValue<NumericResult>(stats, "numeric") ??
                                     NumericResult.Empty;
        HistogramResult histogramResult = GetResultValue<HistogramResult>(stats, "histogram") ??
                                          HistogramResult.Empty;
        QuantileResult? quantileResult = GetResultValue<QuantileResult>(stats, "quantile");

        return new NumericFeatureManifest
        {
            Name = name,
            Kind = kind,
            Count = count,
            NullCount = nullCount,
            ValidCount = count,
            NullRatio = nullRatio,
            DominantValueRatio = dominantValueRatio,
            MissingRuns = missingRuns,
            EstimatedDistinctCount = distinctCount,
            TopKValues = topK,
            Min = numericResult.Min,
            Max = numericResult.Max,
            Mean = numericResult.Mean,
            Variance = numericResult.Variance,
            StandardDeviation = numericResult.StandardDeviation,
            Skewness = numericResult.Skewness,
            Kurtosis = numericResult.Kurtosis,
            Histogram = new HistogramData(histogramResult.BinEdges, histogramResult.Counts),
            Quantiles = quantileResult is not null
                ? new QuantileData(quantileResult.P01, quantileResult.P05, quantileResult.P25,
                    quantileResult.P50, quantileResult.P75, quantileResult.P95, quantileResult.P99,
                    quantileResult.Iqr, quantileResult.LowerFence, quantileResult.UpperFence,
                    quantileResult.OutlierCount, quantileResult.OutlierRatio)
                : null,
            Entropy = entropyResult?.Value,
            EntropyApproximate = entropyResult?.Approximate,
            ZeroCount = numericResult.ZeroCount,
            ZeroRatio = numericResult.ZeroRatio,
            OutlierCount = numericResult.OutlierCount,
            OutlierRatio = numericResult.OutlierRatio,
            IntegerValued = histogramResult.IntegerValued,
            NonzeroCount = numericResult.ZeroRatio > 0.1 && numericResult.NonzeroCount > 0
                ? numericResult.NonzeroCount : null,
            NonzeroMean = numericResult.ZeroRatio > 0.1 && numericResult.NonzeroCount > 0
                ? numericResult.NonzeroMean : null,
            NonzeroVariance = numericResult.ZeroRatio > 0.1 && numericResult.NonzeroCount > 0
                ? numericResult.NonzeroVariance : null,
            NonzeroStandardDeviation = numericResult.ZeroRatio > 0.1 && numericResult.NonzeroCount > 0
                ? numericResult.NonzeroStandardDeviation : null
        };
    }

    private static StringFeatureManifest BuildStringManifest(
        string name, DataKind kind, long count, long nullCount, double? nullRatio, double? dominantValueRatio, long? missingRuns, long distinctCount,
        IReadOnlyList<FrequencyEntry> topK, EntropyResult? entropyResult, ColumnStatistics stats)
    {
        StringLengthResult stringResult = GetResultValue<StringLengthResult>(stats, "string_length") ??
                                          StringLengthResult.Empty;

        return new StringFeatureManifest
        {
            Name = name,
            Kind = kind,
            Count = count,
            NullCount = nullCount,
            ValidCount = count,
            NullRatio = nullRatio,
            DominantValueRatio = dominantValueRatio,
            MissingRuns = missingRuns,
            EstimatedDistinctCount = distinctCount,
            TopKValues = topK,
            MinLength = stringResult.MinLength,
            MaxLength = stringResult.MaxLength,
            Entropy = entropyResult?.Value,
            EntropyApproximate = entropyResult?.Approximate
        };
    }

    private static VectorFeatureManifest BuildVectorManifest(
        string name, DataKind kind, long count, long nullCount, double? nullRatio, double? dominantValueRatio, long? missingRuns, long distinctCount,
        IReadOnlyList<FrequencyEntry> topK, ColumnStatistics stats)
    {
        VectorStatsResult vectorResult = GetResultValue<VectorStatsResult>(stats, "vector_stats") ?? VectorStatsResult.Empty;

        return new VectorFeatureManifest
        {
            Name = name,
            Kind = kind,
            Count = count,
            NullCount = nullCount,
            ValidCount = count,
            NullRatio = nullRatio,
            DominantValueRatio = dominantValueRatio,
            MissingRuns = missingRuns,
            EstimatedDistinctCount = distinctCount,
            TopKValues = topK,
            MinLength = vectorResult.MinElementCount,
            MaxLength = vectorResult.MaxElementCount,
            ElementStats = ToSummaryData(vectorResult.ElementStats),
            ZeroElementCount = vectorResult.ZeroElementCount,
            ZeroElementRatio = vectorResult.ZeroElementRatio,
            ZeroVectorCount = vectorResult.ZeroVectorCount,
            NormMin = vectorResult.NormMin,
            NormMax = vectorResult.NormMax,
            NormMean = vectorResult.NormMean
        };
    }

    private static TensorFeatureManifest BuildTensorManifest(
        string name, DataKind kind, long count, long nullCount, double? nullRatio, double? dominantValueRatio, long? missingRuns, long distinctCount,
        IReadOnlyList<FrequencyEntry> topK, ColumnStatistics stats)
    {
        VectorStatsResult vectorResult = GetResultValue<VectorStatsResult>(stats, "vector_stats") ?? VectorStatsResult.Empty;

        return new TensorFeatureManifest
        {
            Name = name,
            Kind = kind,
            Count = count,
            NullCount = nullCount,
            ValidCount = count,
            NullRatio = nullRatio,
            DominantValueRatio = dominantValueRatio,
            MissingRuns = missingRuns,
            EstimatedDistinctCount = distinctCount,
            TopKValues = topK,
            MinRank = vectorResult.MinRank,
            MaxRank = vectorResult.MaxRank,
            MinElementCount = vectorResult.MinElementCount,
            MaxElementCount = vectorResult.MaxElementCount,
            ElementStats = ToSummaryData(vectorResult.ElementStats),
            ZeroElementCount = vectorResult.ZeroElementCount,
            ZeroElementRatio = vectorResult.ZeroElementRatio,
            ZeroVectorCount = vectorResult.ZeroVectorCount,
            NormMin = vectorResult.NormMin,
            NormMax = vectorResult.NormMax,
            NormMean = vectorResult.NormMean
        };
    }

    private static ImageFeatureManifest BuildImageManifest(
        string name, DataKind kind, long count, long nullCount, double? nullRatio, double? dominantValueRatio, long? missingRuns, long distinctCount,
        IReadOnlyList<FrequencyEntry> topK, ColumnStatistics stats)
    {
        ImageStatsResult imageResult = GetResultValue<ImageStatsResult>(stats, "image_stats") ??
                                       ImageStatsResult.Empty;

        return new ImageFeatureManifest
        {
            Name = name,
            Kind = kind,
            Count = count,
            NullCount = nullCount,
            ValidCount = count,
            NullRatio = nullRatio,
            DominantValueRatio = dominantValueRatio,
            MissingRuns = missingRuns,
            EstimatedDistinctCount = distinctCount,
            TopKValues = topK,
            MinWidth = imageResult.MinWidth,
            MaxWidth = imageResult.MaxWidth,
            MinHeight = imageResult.MinHeight,
            MaxHeight = imageResult.MaxHeight,
            ChannelCounts = imageResult.ChannelCounts,
            OrientationCounts = imageResult.OrientationCounts,
            UndecodableCount = imageResult.UndecodableCount,
            TinyImageCount = imageResult.TinyImageCount,
            HugeImageCount = imageResult.HugeImageCount,
            FileSizeStats = ToSummaryData(imageResult.FileSizeStats),
            MegapixelStats = imageResult.MegapixelStats.Count > 0
                ? ToSummaryData(imageResult.MegapixelStats)
                : null,
            PixelCountStats = imageResult.PixelCountStats.Count > 0
                ? ToSummaryData(imageResult.PixelCountStats)
                : null,
            AspectRatioStats = imageResult.AspectRatioStats.Count > 0
                ? ToSummaryData(imageResult.AspectRatioStats)
                : null,
            AspectRatioHistogram = imageResult.AspectRatioHistogram is { } arh
                ? new HistogramData(arh.BinEdges, arh.Counts)
                : null
        };
    }

    private static BinaryFeatureManifest BuildBinaryManifest(
        string name, DataKind kind, long count, long nullCount, double? nullRatio, double? dominantValueRatio, long? missingRuns, long distinctCount,
        IReadOnlyList<FrequencyEntry> topK, ColumnStatistics stats)
    {
        BinarySizeResult binaryResult = GetResultValue<BinarySizeResult>(stats, "binary_size") ??
                                        BinarySizeResult.Empty;

        return new BinaryFeatureManifest
        {
            Name = name,
            Kind = kind,
            Count = count,
            NullCount = nullCount,
            ValidCount = count,
            NullRatio = nullRatio,
            DominantValueRatio = dominantValueRatio,
            MissingRuns = missingRuns,
            EstimatedDistinctCount = distinctCount,
            TopKValues = topK,
            SizeStats = ToSummaryData(binaryResult.SizeStats)
        };
    }

    private static TemporalFeatureManifest BuildTemporalManifest(
        string name, DataKind kind, long count, long nullCount, double? nullRatio, double? dominantValueRatio, long? missingRuns, long distinctCount,
        IReadOnlyList<FrequencyEntry> topK, EntropyResult? entropyResult, ColumnStatistics stats)
    {
        TemporalRangeResult temporalResult = GetResultValue<TemporalRangeResult>(stats, "temporal_range") ??
                                              TemporalRangeResult.Empty;

        return new TemporalFeatureManifest
        {
            Name = name,
            Kind = kind,
            Count = count,
            NullCount = nullCount,
            ValidCount = count,
            NullRatio = nullRatio,
            DominantValueRatio = dominantValueRatio,
            MissingRuns = missingRuns,
            EstimatedDistinctCount = distinctCount,
            TopKValues = topK,
            Earliest = temporalResult.Earliest,
            Latest = temporalResult.Latest,
            Entropy = entropyResult?.Value,
            EntropyApproximate = entropyResult?.Approximate
        };
    }

    private static StringFeatureManifest BuildFallbackManifest(
        string name, DataKind kind, long count, long nullCount, double? nullRatio, double? dominantValueRatio, long? missingRuns, long distinctCount,
        IReadOnlyList<FrequencyEntry> topK)
    {
        return new StringFeatureManifest
        {
            Name = name,
            Kind = kind,
            Count = count,
            NullCount = nullCount,
            ValidCount = count,
            NullRatio = nullRatio,
            DominantValueRatio = dominantValueRatio,
            MissingRuns = missingRuns,
            EstimatedDistinctCount = distinctCount,
            TopKValues = topK,
            MinLength = 0,
            MaxLength = 0
        };
    }

    private static T? GetResultValue<T>(ColumnStatistics stats, string resultName) where T : class
    {
        if (stats.Results.TryGetValue(resultName, out StatisticResult? result))
        {
            return result.Value as T;
        }

        return null;
    }

    private static IReadOnlyList<FrequencyEntry> MapTopK(TopKResult? topKResult)
    {
        if (topKResult is null)
        {
            return [];
        }

        List<FrequencyEntry> entries = new(topKResult.Entries.Count);

        foreach (KeyValuePair<string, long> entry in topKResult.Entries)
        {
            entries.Add(new FrequencyEntry(entry.Key, entry.Value));
        }

        return entries;
    }

    private static NumericSummaryData ToSummaryData(NumericSummary summary)
    {
        return new NumericSummaryData(
            summary.Count, summary.Min, summary.Max,
            summary.Mean, summary.Variance, summary.StandardDeviation);
    }

    /// <summary>
    /// Generates per-column index type hints from feature statistics.
    /// Boolean and low-cardinality columns (≤ <see cref="IndexConstants.BitmapAutoThreshold"/>)
    /// receive <see cref="IndexHintType.Bitmap"/>, very high cardinality columns
    /// (> <see cref="IndexConstants.BPlusTreeAutoThreshold"/>) receive <see cref="IndexHintType.BTree"/>,
    /// and remaining auto-indexable columns receive <see cref="IndexHintType.Sorted"/>.
    /// Columns whose kind is not auto-indexable receive no hint.
    /// </summary>
    private static IReadOnlyList<ColumnIndexHint>? GenerateIndexHints(
        IReadOnlyList<FeatureManifest> features,
        IReadOnlyDictionary<string, DataKind> columnKinds)
    {
        List<ColumnIndexHint>? hints = null;

        foreach (FeatureManifest feature in features)
        {
            DataKind kind = columnKinds.TryGetValue(feature.Name, out DataKind k) ? k : DataKind.String;

            if (!SourceIndexBuilder.IsAutoIndexableKind(kind))
            {
                continue;
            }

            IndexHintType hintType;

            if (kind is DataKind.Boolean || feature.EstimatedDistinctCount <= IndexConstants.BitmapAutoThreshold)
            {
                hintType = IndexHintType.Bitmap;
            }
            else if (feature.EstimatedDistinctCount > IndexConstants.BPlusTreeAutoThreshold)
            {
                hintType = IndexHintType.BTree;
            }
            else
            {
                hintType = IndexHintType.Sorted;
            }

            hints ??= new List<ColumnIndexHint>();
            hints.Add(new ColumnIndexHint(feature.Name, hintType));
        }

        return hints is { Count: > 0 } ? hints : null;
    }
}
