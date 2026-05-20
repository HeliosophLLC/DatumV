namespace Heliosoph.DatumV.Manifest;

using Heliosoph.DatumV.Indexing;
using Heliosoph.DatumV.Manifest.Insights;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Statistics;
using Heliosoph.DatumV.Statistics.Accumulators;
using Heliosoph.DatumV.Statistics.Interactions;

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
    /// <summary>
    /// Backward-compat overload. Accepts a <see cref="DataKind"/> map and synthesizes
    /// scalar (<see cref="ColumnInfo.IsArray"/>=false) <see cref="ColumnInfo"/>s for each
    /// entry. For byte-array columns (or other typed-array columns) where IsArray must
    /// be set, use the <see cref="ColumnInfo"/> overload directly.
    /// </summary>
    public static QueryResultsManifest Build(
        IReadOnlyDictionary<string, ColumnStatistics> statistics,
        IReadOnlyDictionary<string, DataKind> columnKinds,
        long rowCount,
        IReadOnlyList<ColumnInteractionResult>? interactions = null,
        InsightThresholds? insightThresholds = null)
    {
        Dictionary<string, ColumnInfo> columns = new(columnKinds.Count);
        foreach (KeyValuePair<string, DataKind> entry in columnKinds)
        {
            columns[entry.Key] = new ColumnInfo(entry.Key, entry.Value, nullable: true);
        }
        return Build(statistics, columns, rowCount, interactions, insightThresholds);
    }

    /// <summary>
    /// Builds a manifest from per-column statistics and full column descriptors.
    /// </summary>
    /// <param name="statistics">Per-column statistics from <see cref="StatisticsCollector"/>.</param>
    /// <param name="columns">Map of column name to <see cref="ColumnInfo"/>; carries <see cref="DataKind"/> + <see cref="ColumnInfo.IsArray"/> for byte-array / typed-array dispatch.</param>
    /// <param name="rowCount">Total number of rows in the result set.</param>
    /// <param name="interactions">Optional pairwise column interaction results.</param>
    /// <param name="insightThresholds">Optional thresholds for the insight analysis engine. Pass null to disable insights.</param>
    public static QueryResultsManifest Build(
        IReadOnlyDictionary<string, ColumnStatistics> statistics,
        IReadOnlyDictionary<string, ColumnInfo> columns,
        long rowCount,
        IReadOnlyList<ColumnInteractionResult>? interactions = null,
        InsightThresholds? insightThresholds = null)
    {
        List<FeatureManifest> features = new();

        foreach (KeyValuePair<string, ColumnStatistics> entry in statistics)
        {
            ColumnInfo column = columns.TryGetValue(entry.Key, out ColumnInfo? c)
                ? c
                : new ColumnInfo(entry.Key, DataKind.String, nullable: true);
            FeatureManifest featureManifest = BuildFeature(column, entry.Value, rowCount);
            featureManifest = WithRole(featureManifest, rowCount);
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
            IndexHints = GenerateIndexHints(features, columns)
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

    /// <summary>
    /// Sets the <see cref="FeatureManifest.Role"/> on a fully-constructed manifest
    /// by running the <see cref="ColumnRoleClassifier"/>.
    /// </summary>
    private static FeatureManifest WithRole(FeatureManifest manifest, long rowCount)
    {
        manifest.Role = ColumnRoleClassifier.Classify(manifest, rowCount);
        return manifest;
    }


    private static FeatureManifest BuildFeature(ColumnInfo column, ColumnStatistics stats, long rowCount)
    {
        string name = column.Name;
        DataKind kind = column.Kind;
        bool isArray = column.IsArray;

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

        // Byte arrays (UInt8 + IsArray) take the binary-manifest path; route here
        // before the scalar arms below so they don't fall into the numeric branch.
        if (column.IsByteArrayColumn)
        {
            return BuildBinaryManifest(name, kind, count, nullCount, nullRatio, dominantValueRatio, missingRuns, distinctCount, topK, stats);
        }

        // Any other typed-array column (Float32 + IsArray today; other element kinds
        // when their accumulators land) takes the array-manifest path. Must come
        // before the scalar arms so e.g. Float32 + IsArray hits the array branch
        // rather than NumericFeatureManifest.
        if (isArray)
        {
            return BuildArrayManifest(name, kind, count, nullCount, nullRatio, dominantValueRatio, missingRuns, distinctCount, topK, stats);
        }

        return kind switch
        {
            // Int128/UInt128 are routed through Numeric for v1; the double intermediate
            // loses precision past 2^53 but Min/Max/Mean/etc. remain useful for planner
            // and ML-feature consumers. Dedicated 128-bit accumulator is a follow-up.
            // Duration is routed here too — NumericAccumulator extracts TotalSeconds, so
            // Min/Max/Mean values in the manifest are seconds.
            DataKind.Float16 or DataKind.Float32 or DataKind.Float64
                or DataKind.UInt8 or DataKind.UInt16 or DataKind.UInt32 or DataKind.UInt64 or DataKind.UInt128
                or DataKind.Int8 or DataKind.Int16 or DataKind.Int32 or DataKind.Int64 or DataKind.Int128
                or DataKind.Duration
                => BuildNumericManifest(name, kind, count, nullCount, nullRatio, dominantValueRatio, missingRuns, distinctCount, topK, entropyResult, stats),
            DataKind.Decimal => BuildDecimalManifest(name, count, nullCount, nullRatio, dominantValueRatio, missingRuns, distinctCount, topK, entropyResult, stats),
            DataKind.Uuid => BuildUuidManifest(name, count, nullCount, nullRatio, dominantValueRatio, missingRuns, distinctCount, topK, entropyResult, stats),
            DataKind.Json => BuildJsonManifest(name, count, nullCount, nullRatio, dominantValueRatio, missingRuns, distinctCount, topK, entropyResult, stats),
            DataKind.String => BuildStringManifest(name, kind, count, nullCount, nullRatio, dominantValueRatio, missingRuns, distinctCount, topK, entropyResult, stats),
            DataKind.Image => BuildImageManifest(name, kind, count, nullCount, nullRatio, dominantValueRatio, missingRuns, distinctCount, topK, stats),
            DataKind.Date or DataKind.Timestamp or DataKind.TimestampTz or DataKind.Time => BuildTemporalManifest(name, kind, count, nullCount, nullRatio, dominantValueRatio, missingRuns, distinctCount, topK, entropyResult, stats),
            DataKind.Boolean => BuildBooleanManifest(name, count, nullCount, nullRatio, dominantValueRatio, missingRuns, distinctCount, topK, entropyResult),
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

    private static DecimalFeatureManifest BuildDecimalManifest(
        string name, long count, long nullCount, double? nullRatio, double? dominantValueRatio, long? missingRuns, long distinctCount,
        IReadOnlyList<FrequencyEntry> topK, EntropyResult? entropyResult, ColumnStatistics stats)
    {
        DecimalNumericResult result = GetResultValue<DecimalNumericResult>(stats, "decimal_numeric")
            ?? DecimalNumericResult.Empty;

        return new DecimalFeatureManifest
        {
            Name = name,
            Kind = DataKind.Decimal,
            Count = count,
            NullCount = nullCount,
            ValidCount = count,
            NullRatio = nullRatio,
            DominantValueRatio = dominantValueRatio,
            MissingRuns = missingRuns,
            EstimatedDistinctCount = distinctCount,
            TopKValues = topK,
            Entropy = entropyResult?.Value,
            EntropyApproximate = entropyResult?.Approximate,
            Min = result.Min,
            Max = result.Max,
            Mean = result.Mean,
            Variance = result.Variance,
            StandardDeviation = result.StandardDeviation,
            ZeroCount = result.ZeroCount,
            ZeroRatio = result.ZeroRatio,
            IntegerValued = result.IntegerValued,
        };
    }

    private static UuidFeatureManifest BuildUuidManifest(
        string name, long count, long nullCount, double? nullRatio, double? dominantValueRatio, long? missingRuns, long distinctCount,
        IReadOnlyList<FrequencyEntry> topK, EntropyResult? entropyResult, ColumnStatistics stats)
    {
        UuidStatsResult result = GetResultValue<UuidStatsResult>(stats, "uuid_stats")
            ?? UuidStatsResult.Empty;

        return new UuidFeatureManifest
        {
            Name = name,
            Kind = DataKind.Uuid,
            Count = count,
            NullCount = nullCount,
            ValidCount = count,
            NullRatio = nullRatio,
            DominantValueRatio = dominantValueRatio,
            MissingRuns = missingRuns,
            EstimatedDistinctCount = distinctCount,
            TopKValues = topK,
            Entropy = entropyResult?.Value,
            EntropyApproximate = entropyResult?.Approximate,
            VersionCounts = result.VersionCounts,
            EmbeddedTimestampEarliest = result.EmbeddedTimestampEarliest?.ToString("O"),
            EmbeddedTimestampLatest = result.EmbeddedTimestampLatest?.ToString("O"),
        };
    }

    private static JsonFeatureManifest BuildJsonManifest(
        string name, long count, long nullCount, double? nullRatio, double? dominantValueRatio, long? missingRuns, long distinctCount,
        IReadOnlyList<FrequencyEntry> topK, EntropyResult? entropyResult, ColumnStatistics stats)
    {
        JsonStatsResult result = GetResultValue<JsonStatsResult>(stats, "json_stats")
            ?? JsonStatsResult.Empty;

        return new JsonFeatureManifest
        {
            Name = name,
            Kind = DataKind.Json,
            Count = count,
            NullCount = nullCount,
            ValidCount = count,
            NullRatio = nullRatio,
            DominantValueRatio = dominantValueRatio,
            MissingRuns = missingRuns,
            EstimatedDistinctCount = distinctCount,
            TopKValues = topK,
            Entropy = entropyResult?.Value,
            EntropyApproximate = entropyResult?.Approximate,
            RootTypeCounts = result.RootTypeCounts,
            TopLevelFieldCounts = result.TopLevelFieldCounts,
            MaxDepth = result.MaxDepth,
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
            CharacterClass = ClassifyCharacterClass(topK),
            Entropy = entropyResult?.Value,
            EntropyApproximate = entropyResult?.Approximate
        };
    }

    private static ArrayFeatureManifest BuildArrayManifest(
        string name, DataKind kind, long count, long nullCount, double? nullRatio, double? dominantValueRatio, long? missingRuns, long distinctCount,
        IReadOnlyList<FrequencyEntry> topK, ColumnStatistics stats)
    {
        ArrayStatsResult arrayResult = GetResultValue<ArrayStatsResult>(stats, "array_stats") ?? ArrayStatsResult.Empty;

        return new ArrayFeatureManifest
        {
            Name = name,
            Kind = kind,
            IsArray = true,
            Count = count,
            NullCount = nullCount,
            ValidCount = count,
            NullRatio = nullRatio,
            DominantValueRatio = dominantValueRatio,
            MissingRuns = missingRuns,
            EstimatedDistinctCount = distinctCount,
            TopKValues = topK,
            MinLength = arrayResult.MinElementCount,
            MaxLength = arrayResult.MaxElementCount,
            ElementStats = ToSummaryData(arrayResult.ElementStats),
            ZeroElementCount = arrayResult.ZeroElementCount,
            ZeroElementRatio = arrayResult.ZeroElementRatio,
            ZeroArrayCount = arrayResult.ZeroArrayCount,
            NormMin = arrayResult.NormMin,
            NormMax = arrayResult.NormMax,
            NormMean = arrayResult.NormMean
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
            // DataKind.Image is its own scalar kind (a single encoded blob per
            // value), not a typed array. The IsArray flag means "Kind+IsArray
            // typed-array column" — image columns never satisfy that.
            IsArray = false,
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
            IsArray = true,
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

    /// <summary>
    /// Builds a <see cref="BooleanFeatureManifest"/> for native boolean columns.
    /// Computes <see cref="BooleanFeatureManifest.TrueRatio"/> from top-K frequencies.
    /// </summary>
    private static BooleanFeatureManifest BuildBooleanManifest(
        string name, long count, long nullCount, double? nullRatio, double? dominantValueRatio, long? missingRuns, long distinctCount,
        IReadOnlyList<FrequencyEntry> topK, EntropyResult? entropyResult)
    {
        double trueRatio = 0.0;

        if (count > 0)
        {
            foreach (FrequencyEntry entry in topK)
            {
                if (entry.Value is "true" or "True" or "1")
                {
                    trueRatio = (double)entry.Frequency / count;
                    break;
                }
            }
        }

        return new BooleanFeatureManifest
        {
            Name = name,
            Kind = DataKind.Boolean,
            Count = count,
            NullCount = nullCount,
            ValidCount = count,
            NullRatio = nullRatio,
            DominantValueRatio = dominantValueRatio,
            MissingRuns = missingRuns,
            EstimatedDistinctCount = distinctCount,
            TopKValues = topK,
            Entropy = entropyResult?.Value,
            EntropyApproximate = entropyResult?.Approximate,
            TrueRatio = trueRatio
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

    /// <summary>
    /// Classifies the dominant character repertoire of a string column by inspecting
    /// its top-K values. Returns <see cref="CharacterClass.Mixed"/> when there are
    /// no samples or the values contain characters outside the restricted sets.
    /// </summary>
    internal static CharacterClass ClassifyCharacterClass(IReadOnlyList<FrequencyEntry> topKValues)
    {
        if (topKValues.Count == 0)
        {
            return CharacterClass.Mixed;
        }

        bool allHex = true;
        bool allBase64 = true;
        bool allAlphanumeric = true;

        foreach (FrequencyEntry entry in topKValues)
        {
            if (entry.Value.Length == 0)
            {
                continue;
            }

            foreach (char character in entry.Value)
            {
                bool isDigit = character is >= '0' and <= '9';
                bool isLower = character is >= 'a' and <= 'z';
                bool isUpper = character is >= 'A' and <= 'Z';
                bool isHexLower = character is >= 'a' and <= 'f';
                bool isHexUpper = character is >= 'A' and <= 'F';

                if (!isDigit && !isLower && !isUpper)
                {
                    allAlphanumeric = false;

                    if (character is not '+' and not '/' and not '=')
                    {
                        allBase64 = false;
                    }
                }

                if (!isDigit && !isHexLower && !isHexUpper)
                {
                    allHex = false;
                }

                if (!allHex && !allBase64 && !allAlphanumeric)
                {
                    return CharacterClass.Mixed;
                }
            }
        }

        if (allHex)
        {
            return CharacterClass.Hexadecimal;
        }

        if (allAlphanumeric)
        {
            return CharacterClass.Alphanumeric;
        }

        if (allBase64)
        {
            return CharacterClass.Base64;
        }

        return CharacterClass.Mixed;
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
        IReadOnlyDictionary<string, ColumnInfo> columns)
    {
        List<ColumnIndexHint>? hints = null;

        foreach (FeatureManifest feature in features)
        {
            ColumnInfo? column = columns.TryGetValue(feature.Name, out ColumnInfo? c) ? c : null;
            DataKind kind = column?.Kind ?? DataKind.String;

            // Don't auto-index typed-array columns — array values aren't a useful key.
            if (column?.IsArray == true || !SourceIndexBuilder.IsAutoIndexableKind(kind))
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
