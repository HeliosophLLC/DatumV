namespace DatumIngest.Tests.Manifest;

using DatumIngest.Indexing;
using DatumIngest.Manifest;
using DatumIngest.Model;

/// <summary>
/// PR14h: tests that live column statistics derived from a SourceIndex's
/// per-chunk state correctly overlay onto a cached <see cref="QueryResultsManifest"/>.
/// </summary>
public sealed class LiveManifestOverlayTests
{
    [Fact]
    public void ComputeFromIndex_SumsChunkRowAndNullCounts()
    {
        SourceIndex index = BuildIndex(("score", new ChunkColumnStatistics[]
        {
            new(Minimum: DataValue.FromFloat32(0.1f),
                Maximum: DataValue.FromFloat32(9.9f),
                NullCount: 5, RowCount: 100, EstimatedCardinality: 80),
            new(Minimum: DataValue.FromFloat32(0.2f),
                Maximum: DataValue.FromFloat32(8.8f),
                NullCount: 3, RowCount: 50, EstimatedCardinality: 40),
        }));

        LiveColumnStats? live = LiveColumnStats.ComputeFromIndex(index, "score");

        Assert.NotNull(live);
        Assert.Equal(142, live!.Count);          // (100 - 5) + (50 - 3)
        Assert.Equal(8, live.NullCount);          // 5 + 3
        Assert.Equal(120, live.EstimatedDistinctCount); // 80 + 40, capped at 150
    }

    [Fact]
    public void ComputeFromIndex_CardinalityCappedAtTotal()
    {
        // Chunk-level cardinality estimates can be optimistic — sum may exceed
        // the actual row count when the same value appears in multiple chunks.
        // Cap at total rows.
        SourceIndex index = BuildIndex(("category", new ChunkColumnStatistics[]
        {
            new(Minimum: null, Maximum: null,
                NullCount: 0, RowCount: 100, EstimatedCardinality: 150),
            new(Minimum: null, Maximum: null,
                NullCount: 0, RowCount: 100, EstimatedCardinality: 150),
        }));

        LiveColumnStats? live = LiveColumnStats.ComputeFromIndex(index, "category");

        Assert.NotNull(live);
        Assert.Equal(200, live!.Count);
        // Sum of cardinalities (300) capped at total rows (200).
        Assert.Equal(200, live.EstimatedDistinctCount);
    }

    [Fact]
    public void ComputeFromIndex_ReturnsNull_ForUnknownColumn()
    {
        SourceIndex index = BuildIndex();

        LiveColumnStats? live = LiveColumnStats.ComputeFromIndex(index, "missing");

        Assert.Null(live);
    }

    [Fact]
    public void Compose_OverlaysLiveCount_KeepsCachedExpensiveFields()
    {
        // Cached has a stale row count (10) but the index reflects fresh state
        // (20 rows). After Compose, the manifest exposes 20.
        QueryResultsManifest cached = new()
        {
            RowCount = 10,
            GeneratedAtUtc = DateTime.UtcNow,
            Features =
            [
                new NumericFeatureManifest
                {
                    Name = "score",
                    Kind = DataKind.Float32,
                    Count = 10,
                    NullCount = 0,
                    ValidCount = 10,
                    EstimatedDistinctCount = 10,
                    TopKValues = [new FrequencyEntry("0.5", 3)],
                    NullRatio = 0.0,
                    DominantValueRatio = 0.3,
                    Min = 0.1, Max = 0.9, Mean = 0.5,
                    Variance = 0.05, StandardDeviation = 0.22,
                    Skewness = 0.0, Kurtosis = 3.0,
                    Histogram = new HistogramData([0.0, 0.5, 1.0], [5, 5]),
                    ZeroCount = 0, ZeroRatio = 0.0,
                    OutlierCount = 0, OutlierRatio = 0.0,
                    IntegerValued = false,
                },
            ],
        };

        SourceIndex index = BuildIndex(("score", new ChunkColumnStatistics[]
        {
            new(Minimum: DataValue.FromFloat32(0.1f),
                Maximum: DataValue.FromFloat32(0.9f),
                NullCount: 2, RowCount: 22, EstimatedCardinality: 18),
        }));

        QueryResultsManifest composed = LiveManifestOverlay.Compose(cached, index);

        // Live: row count overridden, count + null + distinct fresh.
        Assert.Equal(22, composed.RowCount);
        NumericFeatureManifest live = Assert.IsType<NumericFeatureManifest>(composed.Features[0]);
        Assert.Equal(20, live.Count);             // 22 - 2
        Assert.Equal(2, live.NullCount);
        Assert.Equal(20, live.ValidCount);
        Assert.Equal(18, live.EstimatedDistinctCount);

        // Cached fields preserved verbatim.
        Assert.Equal(0.5, live.Mean);
        Assert.Equal(0.22, live.StandardDeviation, 1e-10);
        Assert.NotNull(live.Histogram);
        Assert.Equal(2, live.Histogram.Counts.Count);
        Assert.Single(live.TopKValues);
    }

    [Fact]
    public void Compose_PassesThroughColumns_WithoutLiveStats()
    {
        // A column that doesn't appear in the index's chunk statistics is
        // returned unchanged.
        QueryResultsManifest cached = new()
        {
            RowCount = 5,
            GeneratedAtUtc = DateTime.UtcNow,
            Features =
            [
                new StringFeatureManifest
                {
                    Name = "label",
                    Kind = DataKind.String,
                    Count = 5,
                    NullCount = 0,
                    ValidCount = 5,
                    EstimatedDistinctCount = 5,
                    TopKValues = [],
                    MinLength = 1,
                    MaxLength = 7,
                },
            ],
        };

        SourceIndex index = BuildIndex(); // Empty — no chunks.

        QueryResultsManifest composed = LiveManifestOverlay.Compose(cached, index);

        Assert.Equal(5, composed.RowCount); // Cached row count preserved.
        Assert.Same(cached.Features[0], composed.Features[0]);
    }

    [Fact]
    public void FeatureManifest_CachedStatsValid_DefaultsToTrue()
    {
        StringFeatureManifest feature = new()
        {
            Name = "x",
            Kind = DataKind.String,
            Count = 0,
            NullCount = 0,
            ValidCount = 0,
            EstimatedDistinctCount = 0,
            TopKValues = [],
            MinLength = 0,
            MaxLength = 0,
        };

        Assert.True(feature.CachedStatsValid);
    }

    private static SourceIndex BuildIndex(params (string column, ChunkColumnStatistics[] perChunkStats)[] columns)
    {
        // Find the longest column's chunk count.
        int chunkCount = 0;
        foreach ((_, ChunkColumnStatistics[] stats) in columns)
        {
            if (stats.Length > chunkCount) chunkCount = stats.Length;
        }

        List<IndexChunk> chunks = new();
        long offset = 0;
        for (int c = 0; c < chunkCount; c++)
        {
            Dictionary<string, ChunkColumnStatistics> stats = new(StringComparer.OrdinalIgnoreCase);
            long rowCount = 0;
            foreach ((string name, ChunkColumnStatistics[] perColumn) in columns)
            {
                if (c < perColumn.Length)
                {
                    stats[name] = perColumn[c];
                    if (perColumn[c].RowCount > rowCount) rowCount = perColumn[c].RowCount;
                }
            }
            chunks.Add(new IndexChunk(RowOffset: offset, RowCount: rowCount, ColumnStatistics: stats));
            offset += rowCount;
        }

        // Schema requires at least one column; synthesize a placeholder when
        // the test passes no column tuples (e.g. the "unknown column" path).
        ColumnInfo[] schemaColumns = columns.Length == 0
            ? [new ColumnInfo("_placeholder", DataKind.Int32, nullable: true)]
            : new ColumnInfo[columns.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            schemaColumns[i] = new ColumnInfo(columns[i].column, DataKind.Float32, nullable: true);
        }
        Schema schema = new(schemaColumns);
        IndexSchema indexSchema = new(schema, offset);

        SourceFingerprint fingerprint = new(0, new byte[32]);
        return new SourceIndex(fingerprint, indexSchema, chunks);
    }
}
