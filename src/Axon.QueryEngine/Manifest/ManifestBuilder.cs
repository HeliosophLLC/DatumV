namespace Axon.QueryEngine.Manifest;

using Axon.QueryEngine.Model;
using Axon.QueryEngine.Statistics;
using Axon.QueryEngine.Statistics.Accumulators;

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
    public static QueryResultsManifest Build(
        IReadOnlyDictionary<string, ColumnStatistics> statistics,
        IReadOnlyDictionary<string, DataKind> columnKinds,
        long rowCount)
    {
        List<FeatureManifest> features = new();

        foreach (KeyValuePair<string, ColumnStatistics> entry in statistics)
        {
            DataKind kind = columnKinds.TryGetValue(entry.Key, out DataKind k) ? k : DataKind.String;
            FeatureManifest manifest = BuildFeature(entry.Key, kind, entry.Value);
            features.Add(manifest);
        }

        return new QueryResultsManifest
        {
            RowCount = rowCount,
            GeneratedAtUtc = DateTime.UtcNow,
            Features = features
        };
    }

    private static FeatureManifest BuildFeature(string name, DataKind kind, ColumnStatistics stats)
    {
        // Extract common fields
        CountResult? countResult = GetResultValue<CountResult>(stats, "count");
        CardinalityResult? cardinalityResult = GetResultValue<CardinalityResult>(stats, "cardinality");
        TopKResult? topKResult = GetResultValue<TopKResult>(stats, "top_k");

        long count = countResult?.NonNull ?? 0;
        long nullCount = countResult?.NullOrEmpty ?? 0;
        long distinctCount = cardinalityResult?.EstimatedDistinctCount ?? 0;
        IReadOnlyList<FrequencyEntry> topK = MapTopK(topKResult);

        return kind switch
        {
            DataKind.Scalar or DataKind.UInt8 => BuildNumericManifest(name, kind, count, nullCount, distinctCount, topK, stats),
            DataKind.String or DataKind.JsonValue => BuildStringManifest(name, kind, count, nullCount, distinctCount, topK, stats),
            DataKind.Vector => BuildVectorManifest(name, kind, count, nullCount, distinctCount, topK, stats),
            DataKind.Matrix or DataKind.Tensor => BuildTensorManifest(name, kind, count, nullCount, distinctCount, topK, stats),
            DataKind.Image => BuildImageManifest(name, kind, count, nullCount, distinctCount, topK, stats),
            DataKind.UInt8Array => BuildBinaryManifest(name, kind, count, nullCount, distinctCount, topK, stats),
            DataKind.Date or DataKind.DateTime => BuildTemporalManifest(name, kind, count, nullCount, distinctCount, topK, stats),
            _ => BuildFallbackManifest(name, kind, count, nullCount, distinctCount, topK)
        };
    }

    private static NumericFeatureManifest BuildNumericManifest(
        string name, DataKind kind, long count, long nullCount, long distinctCount,
        IReadOnlyList<FrequencyEntry> topK, ColumnStatistics stats)
    {
        NumericResult numericResult = GetResultValue<NumericResult>(stats, "numeric") ??
                                     new NumericResult(0, double.NaN, double.NaN, double.NaN, 0, 0);
        HistogramResult histogramResult = GetResultValue<HistogramResult>(stats, "histogram") ??
                                          new HistogramResult([], []);

        return new NumericFeatureManifest
        {
            Name = name,
            Kind = kind,
            Count = count,
            NullCount = nullCount,
            EstimatedDistinctCount = distinctCount,
            TopKValues = topK,
            Min = numericResult.Min,
            Max = numericResult.Max,
            Mean = numericResult.Mean,
            Variance = numericResult.Variance,
            StandardDeviation = numericResult.StandardDeviation,
            Histogram = new HistogramData(histogramResult.BinEdges, histogramResult.Counts)
        };
    }

    private static StringFeatureManifest BuildStringManifest(
        string name, DataKind kind, long count, long nullCount, long distinctCount,
        IReadOnlyList<FrequencyEntry> topK, ColumnStatistics stats)
    {
        StringLengthResult stringResult = GetResultValue<StringLengthResult>(stats, "string_length") ??
                                          new StringLengthResult(0, 0, 0);

        return new StringFeatureManifest
        {
            Name = name,
            Kind = kind,
            Count = count,
            NullCount = nullCount,
            EstimatedDistinctCount = distinctCount,
            TopKValues = topK,
            MinLength = stringResult.MinLength,
            MaxLength = stringResult.MaxLength
        };
    }

    private static VectorFeatureManifest BuildVectorManifest(
        string name, DataKind kind, long count, long nullCount, long distinctCount,
        IReadOnlyList<FrequencyEntry> topK, ColumnStatistics stats)
    {
        VectorStatsResult vectorResult = GetResultValue<VectorStatsResult>(stats, "vector_stats") ??
                                         new VectorStatsResult(0, 0, 0, 0, 0, new NumericSummary(0, double.NaN, double.NaN, double.NaN, 0, 0));

        return new VectorFeatureManifest
        {
            Name = name,
            Kind = kind,
            Count = count,
            NullCount = nullCount,
            EstimatedDistinctCount = distinctCount,
            TopKValues = topK,
            MinLength = vectorResult.MinElementCount,
            MaxLength = vectorResult.MaxElementCount,
            ElementStats = ToSummaryData(vectorResult.ElementStats)
        };
    }

    private static TensorFeatureManifest BuildTensorManifest(
        string name, DataKind kind, long count, long nullCount, long distinctCount,
        IReadOnlyList<FrequencyEntry> topK, ColumnStatistics stats)
    {
        VectorStatsResult vectorResult = GetResultValue<VectorStatsResult>(stats, "vector_stats") ??
                                         new VectorStatsResult(0, 0, 0, 0, 0, new NumericSummary(0, double.NaN, double.NaN, double.NaN, 0, 0));

        return new TensorFeatureManifest
        {
            Name = name,
            Kind = kind,
            Count = count,
            NullCount = nullCount,
            EstimatedDistinctCount = distinctCount,
            TopKValues = topK,
            MinRank = vectorResult.MinRank,
            MaxRank = vectorResult.MaxRank,
            MinElementCount = vectorResult.MinElementCount,
            MaxElementCount = vectorResult.MaxElementCount,
            ElementStats = ToSummaryData(vectorResult.ElementStats)
        };
    }

    private static ImageFeatureManifest BuildImageManifest(
        string name, DataKind kind, long count, long nullCount, long distinctCount,
        IReadOnlyList<FrequencyEntry> topK, ColumnStatistics stats)
    {
        ImageStatsResult imageResult = GetResultValue<ImageStatsResult>(stats, "image_stats") ??
                                       new ImageStatsResult(0, 0, 0, 0, 0, new Dictionary<int, long>(), 0,
                                           new NumericSummary(0, double.NaN, double.NaN, double.NaN, 0, 0));

        return new ImageFeatureManifest
        {
            Name = name,
            Kind = kind,
            Count = count,
            NullCount = nullCount,
            EstimatedDistinctCount = distinctCount,
            TopKValues = topK,
            MinWidth = imageResult.MinWidth,
            MaxWidth = imageResult.MaxWidth,
            MinHeight = imageResult.MinHeight,
            MaxHeight = imageResult.MaxHeight,
            ChannelCounts = imageResult.ChannelCounts,
            UndecodableCount = imageResult.UndecodableCount,
            FileSizeStats = ToSummaryData(imageResult.FileSizeStats)
        };
    }

    private static BinaryFeatureManifest BuildBinaryManifest(
        string name, DataKind kind, long count, long nullCount, long distinctCount,
        IReadOnlyList<FrequencyEntry> topK, ColumnStatistics stats)
    {
        BinarySizeResult binaryResult = GetResultValue<BinarySizeResult>(stats, "binary_size") ??
                                        new BinarySizeResult(new NumericSummary(0, double.NaN, double.NaN, double.NaN, 0, 0));

        return new BinaryFeatureManifest
        {
            Name = name,
            Kind = kind,
            Count = count,
            NullCount = nullCount,
            EstimatedDistinctCount = distinctCount,
            TopKValues = topK,
            SizeStats = ToSummaryData(binaryResult.SizeStats)
        };
    }

    private static TemporalFeatureManifest BuildTemporalManifest(
        string name, DataKind kind, long count, long nullCount, long distinctCount,
        IReadOnlyList<FrequencyEntry> topK, ColumnStatistics stats)
    {
        TemporalRangeResult temporalResult = GetResultValue<TemporalRangeResult>(stats, "temporal_range") ??
                                              new TemporalRangeResult(null, null);

        return new TemporalFeatureManifest
        {
            Name = name,
            Kind = kind,
            Count = count,
            NullCount = nullCount,
            EstimatedDistinctCount = distinctCount,
            TopKValues = topK,
            Earliest = temporalResult.Earliest,
            Latest = temporalResult.Latest
        };
    }

    private static StringFeatureManifest BuildFallbackManifest(
        string name, DataKind kind, long count, long nullCount, long distinctCount,
        IReadOnlyList<FrequencyEntry> topK)
    {
        return new StringFeatureManifest
        {
            Name = name,
            Kind = kind,
            Count = count,
            NullCount = nullCount,
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
}
