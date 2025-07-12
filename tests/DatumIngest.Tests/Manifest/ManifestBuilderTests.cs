namespace DatumQuery.Tests.Manifest;

using DatumQuery.Manifest;
using DatumQuery.Model;
using DatumQuery.Statistics;
using DatumQuery.Statistics.Accumulators;

public sealed class ManifestBuilderTests
{
    [Fact]
    public void Build_NumericColumn_ProducesNumericFeatureManifest()
    {
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow("value", DataValue.FromScalar(1.0f)));
        collector.AddRow(MakeRow("value", DataValue.FromScalar(2.0f)));
        collector.AddRow(MakeRow("value", DataValue.FromScalar(3.0f)));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["value"] = DataKind.Scalar };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 3);

        Assert.Equal(3, manifest.RowCount);
        Assert.Single(manifest.Features);

        NumericFeatureManifest feature = Assert.IsType<NumericFeatureManifest>(manifest.Features[0]);
        Assert.Equal("value", feature.Name);
        Assert.Equal(DataKind.Scalar, feature.Kind);
        Assert.Equal(3, feature.Count);
        Assert.Equal(0, feature.NullCount);
        Assert.Equal(3, feature.ValidCount);
        Assert.Equal(1.0, feature.Min);
        Assert.Equal(3.0, feature.Max);
        Assert.Equal(2.0, feature.Mean, 1e-10);
        Assert.NotEmpty(feature.Histogram.BinEdges);
    }

    [Fact]
    public void Build_StringColumn_ProducesStringFeatureManifest()
    {
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow("name", DataValue.FromString("Alice")));
        collector.AddRow(MakeRow("name", DataValue.FromString("Bob")));
        collector.AddRow(MakeRow("name", DataValue.FromString("Charlotte")));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["name"] = DataKind.String };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 3);

        StringFeatureManifest feature = Assert.IsType<StringFeatureManifest>(manifest.Features[0]);
        Assert.Equal("name", feature.Name);
        Assert.Equal(DataKind.String, feature.Kind);
        Assert.Equal(3, feature.Count);
        Assert.Equal(3, feature.MinLength);  // "Bob"
        Assert.Equal(9, feature.MaxLength);  // "Charlotte"
        Assert.Equal(3, feature.EstimatedDistinctCount);
    }

    [Fact]
    public void Build_VectorColumn_ProducesVectorFeatureManifest()
    {
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow("embedding", DataValue.FromVector([1.0f, 2.0f, 3.0f])));
        collector.AddRow(MakeRow("embedding", DataValue.FromVector([4.0f, 5.0f])));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["embedding"] = DataKind.Vector };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 2);

        VectorFeatureManifest feature = Assert.IsType<VectorFeatureManifest>(manifest.Features[0]);
        Assert.Equal(2, feature.MinLength);
        Assert.Equal(3, feature.MaxLength);
        Assert.Equal(5, feature.ElementStats.Count);
        Assert.Equal(1.0, feature.ElementStats.Min);
        Assert.Equal(5.0, feature.ElementStats.Max);

        // ||[1,2,3]||₂ = sqrt(14), ||[4,5]||₂ = sqrt(41)
        Assert.Equal(Math.Sqrt(14.0), feature.NormMin, 1e-10);
        Assert.Equal(Math.Sqrt(41.0), feature.NormMax, 1e-10);
        Assert.Equal((Math.Sqrt(14.0) + Math.Sqrt(41.0)) / 2.0, feature.NormMean, 1e-10);
    }

    [Fact]
    public void Build_MatrixColumn_ProducesTensorFeatureManifest()
    {
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow("weights", DataValue.FromMatrix([1.0f, 2.0f, 3.0f, 4.0f], 2, 2)));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["weights"] = DataKind.Matrix };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 1);

        TensorFeatureManifest feature = Assert.IsType<TensorFeatureManifest>(manifest.Features[0]);
        Assert.Equal(2, feature.MinRank);
        Assert.Equal(2, feature.MaxRank);
        Assert.Equal(4, feature.MinElementCount);
        Assert.Equal(4, feature.MaxElementCount);
    }

    [Fact]
    public void Build_ImageColumn_ProducesImageFeatureManifest()
    {
        StatisticsCollector collector = new();
        byte[] jpeg = MakeMinimalJpeg(640, 480, 3);
        collector.AddRow(MakeRow("photo", DataValue.FromImage(jpeg)));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["photo"] = DataKind.Image };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 1);

        ImageFeatureManifest feature = Assert.IsType<ImageFeatureManifest>(manifest.Features[0]);
        Assert.Equal(640, feature.MinWidth);
        Assert.Equal(640, feature.MaxWidth);
        Assert.Equal(480, feature.MinHeight);
        Assert.Equal(480, feature.MaxHeight);
        Assert.Equal(1, feature.ChannelCounts[3]);
        Assert.Equal(1, feature.OrientationCounts["landscape"]);
        Assert.True(feature.FileSizeStats.Count > 0);
        Assert.NotNull(feature.AspectRatioStats);
        Assert.Equal(1, feature.AspectRatioStats.Count);
        Assert.Equal(640.0 / 480.0, feature.AspectRatioStats.Mean, 3);
        Assert.NotNull(feature.MegapixelStats);
        Assert.Equal(1, feature.MegapixelStats.Count);
        Assert.Equal(640.0 * 480 / 1_000_000.0, feature.MegapixelStats.Mean, 4);
        Assert.NotNull(feature.PixelCountStats);
        Assert.Equal(1, feature.PixelCountStats.Count);
        Assert.Equal(640.0 * 480, feature.PixelCountStats.Mean, 0);
    }

    [Fact]
    public void Build_BinaryColumn_ProducesBinaryFeatureManifest()
    {
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow("raw", DataValue.FromUInt8Array([1, 2, 3, 4, 5])));
        collector.AddRow(MakeRow("raw", DataValue.FromUInt8Array([10, 20])));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["raw"] = DataKind.UInt8Array };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 2);

        BinaryFeatureManifest feature = Assert.IsType<BinaryFeatureManifest>(manifest.Features[0]);
        Assert.Equal(2.0, feature.SizeStats.Min);
        Assert.Equal(5.0, feature.SizeStats.Max);
    }

    [Fact]
    public void Build_DateColumn_ProducesTemporalFeatureManifest()
    {
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow("created", DataValue.FromDate(new DateOnly(2024, 1, 15))));
        collector.AddRow(MakeRow("created", DataValue.FromDate(new DateOnly(2025, 6, 30))));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["created"] = DataKind.Date };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 2);

        TemporalFeatureManifest feature = Assert.IsType<TemporalFeatureManifest>(manifest.Features[0]);
        Assert.Equal("2024-01-15", feature.Earliest);
        Assert.Equal("2025-06-30", feature.Latest);
    }

    [Fact]
    public void Build_MultipleColumns_ProducesCorrectTypes()
    {
        StatisticsCollector collector = new();

        Row row = new(
            ["id", "name", "score"],
            [DataValue.FromScalar(1.0f), DataValue.FromString("test"), DataValue.FromUInt8(200)]);

        collector.AddRow(row);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new()
        {
            ["id"] = DataKind.Scalar,
            ["name"] = DataKind.String,
            ["score"] = DataKind.UInt8
        };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 1);

        Assert.Equal(3, manifest.Features.Count);
        Assert.Contains(manifest.Features, f => f is NumericFeatureManifest && f.Name == "id");
        Assert.Contains(manifest.Features, f => f is StringFeatureManifest && f.Name == "name");
        Assert.Contains(manifest.Features, f => f is NumericFeatureManifest && f.Name == "score");
    }

    [Fact]
    public void Build_NullValues_TrackedInNullCount()
    {
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow("value", DataValue.FromScalar(1.0f)));
        collector.AddRow(MakeRow("value", DataValue.Null(DataKind.Scalar)));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["value"] = DataKind.Scalar };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 2);

        NumericFeatureManifest feature = Assert.IsType<NumericFeatureManifest>(manifest.Features[0]);
        Assert.Equal(1, feature.Count);
        Assert.Equal(1, feature.NullCount);
        Assert.Equal(1, feature.ValidCount);
    }

    [Fact]
    public void Build_TopKValues_Populated()
    {
        StatisticsCollector collector = new();

        for (int i = 0; i < 10; i++)
        {
            collector.AddRow(MakeRow("category", DataValue.FromString("A")));
        }

        for (int i = 0; i < 5; i++)
        {
            collector.AddRow(MakeRow("category", DataValue.FromString("B")));
        }

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["category"] = DataKind.String };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 15);

        StringFeatureManifest feature = Assert.IsType<StringFeatureManifest>(manifest.Features[0]);
        Assert.NotEmpty(feature.TopKValues);
        Assert.Equal("A", feature.TopKValues[0].Value);
        Assert.Equal(10, feature.TopKValues[0].Frequency);
    }

    [Fact]
    public void Build_GeneratedAtUtc_IsSet()
    {
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow("x", DataValue.FromScalar(1.0f)));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["x"] = DataKind.Scalar };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 1);

        Assert.True(manifest.GeneratedAtUtc > DateTime.MinValue);
        Assert.True(manifest.GeneratedAtUtc <= DateTime.UtcNow);
    }

    [Fact]
    public void Build_NullRatio_ComputedCorrectly()
    {
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow("value", DataValue.FromScalar(1.0f)));
        collector.AddRow(MakeRow("value", DataValue.Null(DataKind.Scalar)));
        collector.AddRow(MakeRow("value", DataValue.FromScalar(3.0f)));
        collector.AddRow(MakeRow("value", DataValue.Null(DataKind.Scalar)));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["value"] = DataKind.Scalar };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 4);

        NumericFeatureManifest feature = Assert.IsType<NumericFeatureManifest>(manifest.Features[0]);
        Assert.Equal(0.5, feature.NullRatio!.Value, 1e-10);
    }

    [Fact]
    public void Build_NullRatio_ZeroRows_IsNull()
    {
        StatisticsCollector collector = new();

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["value"] = DataKind.Scalar };

        // No columns in stats when no rows added — build with empty stats but rowCount 0
        // Add at least one row so the column exists, then build with rowCount = 0
        collector.AddRow(MakeRow("value", DataValue.FromScalar(1.0f)));
        stats = collector.GetStatistics();

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 0);

        NumericFeatureManifest feature = Assert.IsType<NumericFeatureManifest>(manifest.Features[0]);
        Assert.Null(feature.NullRatio);
    }

    [Fact]
    public void Build_NullRatio_NoNulls_IsZero()
    {
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow("value", DataValue.FromScalar(1.0f)));
        collector.AddRow(MakeRow("value", DataValue.FromScalar(2.0f)));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["value"] = DataKind.Scalar };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 2);

        NumericFeatureManifest feature = Assert.IsType<NumericFeatureManifest>(manifest.Features[0]);
        Assert.Equal(0.0, feature.NullRatio!.Value, 1e-10);
    }

    [Fact]
    public void Build_MissingRuns_TrackedCorrectly()
    {
        StatisticsCollector collector = new();
        // Run 1: [null, null]
        collector.AddRow(MakeRow("value", DataValue.Null(DataKind.Scalar)));
        collector.AddRow(MakeRow("value", DataValue.Null(DataKind.Scalar)));
        // non-null
        collector.AddRow(MakeRow("value", DataValue.FromScalar(1.0f)));
        // Run 2: [null]
        collector.AddRow(MakeRow("value", DataValue.Null(DataKind.Scalar)));
        // non-null
        collector.AddRow(MakeRow("value", DataValue.FromScalar(2.0f)));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["value"] = DataKind.Scalar };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 5);

        NumericFeatureManifest feature = Assert.IsType<NumericFeatureManifest>(manifest.Features[0]);
        Assert.Equal(2, feature.MissingRuns);
    }

    [Fact]
    public void Build_MissingRuns_NoNulls_IsZero()
    {
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow("value", DataValue.FromScalar(1.0f)));
        collector.AddRow(MakeRow("value", DataValue.FromScalar(2.0f)));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["value"] = DataKind.Scalar };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 2);

        NumericFeatureManifest feature = Assert.IsType<NumericFeatureManifest>(manifest.Features[0]);
        Assert.Equal(0, feature.MissingRuns);
    }

    [Fact]
    public void Build_ConstantColumn_IsConstantTrue()
    {
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow("price", DataValue.FromScalar(0.0f)));
        collector.AddRow(MakeRow("price", DataValue.FromScalar(0.0f)));
        collector.AddRow(MakeRow("price", DataValue.FromScalar(0.0f)));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["price"] = DataKind.Scalar };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 3);

        NumericFeatureManifest feature = Assert.IsType<NumericFeatureManifest>(manifest.Features[0]);
        Assert.True(feature.IsConstant);
        Assert.Equal(1, feature.EstimatedDistinctCount);
    }

    [Fact]
    public void Build_VaryingColumn_IsConstantFalse()
    {
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow("price", DataValue.FromScalar(1.0f)));
        collector.AddRow(MakeRow("price", DataValue.FromScalar(2.0f)));
        collector.AddRow(MakeRow("price", DataValue.FromScalar(3.0f)));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["price"] = DataKind.Scalar };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 3);

        NumericFeatureManifest feature = Assert.IsType<NumericFeatureManifest>(manifest.Features[0]);
        Assert.False(feature.IsConstant);
    }

    [Fact]
    public void Build_AllNullColumn_IsConstantTrue()
    {
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow("price", DataValue.Null(DataKind.Scalar)));
        collector.AddRow(MakeRow("price", DataValue.Null(DataKind.Scalar)));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["price"] = DataKind.Scalar };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 2);

        NumericFeatureManifest feature = Assert.IsType<NumericFeatureManifest>(manifest.Features[0]);
        Assert.True(feature.IsConstant);
        Assert.Equal(0, feature.EstimatedDistinctCount);
    }

    [Fact]
    public void Build_DominantValueRatio_NearConstant()
    {
        StatisticsCollector collector = new();

        for (int i = 0; i < 99; i++)
        {
            collector.AddRow(MakeRow("status", DataValue.FromString("active")));
        }

        collector.AddRow(MakeRow("status", DataValue.FromString("inactive")));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["status"] = DataKind.String };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 100);

        StringFeatureManifest feature = Assert.IsType<StringFeatureManifest>(manifest.Features[0]);
        Assert.NotNull(feature.DominantValueRatio);
        Assert.Equal(0.99, feature.DominantValueRatio!.Value, 1e-10);
    }

    [Fact]
    public void Build_DominantValueRatio_UniformDistribution()
    {
        StatisticsCollector collector = new();

        for (int i = 0; i < 20; i++)
        {
            collector.AddRow(MakeRow("cat", DataValue.FromString("A")));
            collector.AddRow(MakeRow("cat", DataValue.FromString("B")));
            collector.AddRow(MakeRow("cat", DataValue.FromString("C")));
            collector.AddRow(MakeRow("cat", DataValue.FromString("D")));
            collector.AddRow(MakeRow("cat", DataValue.FromString("E")));
        }

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["cat"] = DataKind.String };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 100);

        StringFeatureManifest feature = Assert.IsType<StringFeatureManifest>(manifest.Features[0]);
        Assert.NotNull(feature.DominantValueRatio);
        Assert.Equal(0.2, feature.DominantValueRatio!.Value, 1e-10);
    }

    [Fact]
    public void Build_DominantValueRatio_ZeroRows_IsNull()
    {
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow("value", DataValue.FromScalar(1.0f)));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["value"] = DataKind.Scalar };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 0);

        NumericFeatureManifest feature = Assert.IsType<NumericFeatureManifest>(manifest.Features[0]);
        Assert.Null(feature.DominantValueRatio);
    }

    [Fact]
    public void Build_NearConstantColumn_IsNearConstantTrue()
    {
        StatisticsCollector collector = new();

        for (int i = 0; i < 99; i++)
        {
            collector.AddRow(MakeRow("status", DataValue.FromString("active")));
        }

        collector.AddRow(MakeRow("status", DataValue.FromString("inactive")));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["status"] = DataKind.String };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 100);

        StringFeatureManifest feature = Assert.IsType<StringFeatureManifest>(manifest.Features[0]);
        Assert.True(feature.IsNearConstant);
    }

    [Fact]
    public void Build_UniformColumn_IsNearConstantFalse()
    {
        StatisticsCollector collector = new();

        for (int i = 0; i < 20; i++)
        {
            collector.AddRow(MakeRow("cat", DataValue.FromString("A")));
            collector.AddRow(MakeRow("cat", DataValue.FromString("B")));
            collector.AddRow(MakeRow("cat", DataValue.FromString("C")));
            collector.AddRow(MakeRow("cat", DataValue.FromString("D")));
            collector.AddRow(MakeRow("cat", DataValue.FromString("E")));
        }

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["cat"] = DataKind.String };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 100);

        StringFeatureManifest feature = Assert.IsType<StringFeatureManifest>(manifest.Features[0]);
        Assert.False(feature.IsNearConstant);
    }

    [Fact]
    public void Build_ZeroRows_IsNearConstantFalse()
    {
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow("value", DataValue.FromScalar(1.0f)));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["value"] = DataKind.Scalar };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 0);

        NumericFeatureManifest feature = Assert.IsType<NumericFeatureManifest>(manifest.Features[0]);
        Assert.False(feature.IsNearConstant);
    }

    [Fact]
    public void Build_BoundaryRatio_IsNearConstantFalse()
    {
        StatisticsCollector collector = new();

        // 98 of one value + 2 of another in 100 rows = exactly 0.98 ratio
        for (int i = 0; i < 98; i++)
        {
            collector.AddRow(MakeRow("flag", DataValue.FromString("yes")));
        }

        collector.AddRow(MakeRow("flag", DataValue.FromString("no")));
        collector.AddRow(MakeRow("flag", DataValue.FromString("no")));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["flag"] = DataKind.String };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 100);

        StringFeatureManifest feature = Assert.IsType<StringFeatureManifest>(manifest.Features[0]);
        Assert.False(feature.IsNearConstant);
    }

    private static Row MakeRow(string columnName, DataValue value)
    {
        return new Row([columnName], [value]);
    }

    private static byte[] MakeMinimalJpeg(int width, int height, int channels)
    {
        byte[] data = new byte[20];
        data[0] = 0xFF;
        data[1] = 0xD8;
        data[2] = 0xFF;
        data[3] = 0xC0;
        data[4] = 0x00;
        data[5] = 0x0B;
        data[6] = 0x08;
        data[7] = (byte)(height >> 8);
        data[8] = (byte)(height & 0xFF);
        data[9] = (byte)(width >> 8);
        data[10] = (byte)(width & 0xFF);
        data[11] = (byte)channels;
        return data;
    }
}
