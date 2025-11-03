namespace DatumIngest.Tests.Statistics;

using DatumIngest.Model;
using DatumIngest.Statistics;
using DatumIngest.Statistics.Accumulators;

public sealed class StatisticsCollectorTests : ServiceTestBase
{
    private readonly Arena _arena = new();

    public override void Dispose()
    {
        _arena.Dispose();
        base.Dispose();
    }

    [Fact]
    public void AddRow_NumericColumn_CollectsAllStatistics()
    {
        StatisticsCollector collector = new();

        collector.AddRow(CreateRow(("value", DataValue.FromFloat32(1.0f))), _arena);
        collector.AddRow(CreateRow(("value", DataValue.FromFloat32(2.0f))), _arena);
        collector.AddRow(CreateRow(("value", DataValue.FromFloat32(3.0f))), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();

        Assert.Contains("value", stats.Keys);
        ColumnStatistics columnStats = stats["value"];

        Assert.Contains("count", columnStats.Results.Keys);
        Assert.Contains("cardinality", columnStats.Results.Keys);
        Assert.Contains("top_k", columnStats.Results.Keys);
        Assert.Contains("numeric", columnStats.Results.Keys);
        Assert.Contains("quantile", columnStats.Results.Keys);
    }

    [Fact]
    public void AddRow_StringColumn_CollectsStringLength()
    {
        StatisticsCollector collector = new();

        collector.AddRow(CreateRow(("name", DataValue.FromString("Alice", _arena))), _arena);
        collector.AddRow(CreateRow(("name", DataValue.FromString("Bob", _arena))), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();

        Assert.Contains("name", stats.Keys);
        ColumnStatistics columnStats = stats["name"];

        Assert.Contains("count", columnStats.Results.Keys);
        Assert.Contains("cardinality", columnStats.Results.Keys);
        Assert.Contains("top_k", columnStats.Results.Keys);
        Assert.Contains("string_length", columnStats.Results.Keys);
    }

    [Fact]
    public void AddRow_NumericColumn_DoesNotCollectStringLength()
    {
        StatisticsCollector collector = new();

        collector.AddRow(CreateRow(("value", DataValue.FromFloat32(1.0f))), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        ColumnStatistics columnStats = stats["value"];

        Assert.DoesNotContain("string_length", columnStats.Results.Keys);
    }

    [Fact]
    public void AddRow_StringColumn_DoesNotCollectNumeric()
    {
        StatisticsCollector collector = new();

        collector.AddRow(CreateRow(("name", DataValue.FromString("Alice", _arena))), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        ColumnStatistics columnStats = stats["name"];

        Assert.DoesNotContain("numeric", columnStats.Results.Keys);
    }

    [Fact]
    public void AddRow_MultipleColumns_TracksEachIndependently()
    {
        StatisticsCollector collector = new();

        collector.AddRow(CreateRow(
            ("name", DataValue.FromString("Alice", _arena)),
            ("age", DataValue.FromFloat32(30.0f))), _arena);
        collector.AddRow(CreateRow(
            ("name", DataValue.FromString("Bob", _arena)),
            ("age", DataValue.FromFloat32(25.0f))), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();

        Assert.Equal(2, stats.Count);
        Assert.Contains("name", stats.Keys);
        Assert.Contains("age", stats.Keys);

        CountResult nameCount = (CountResult)stats["name"].Results["count"].Value!;
        Assert.Equal(2, nameCount.NonNull);

        NumericResult ageNumeric = (NumericResult)stats["age"].Results["numeric"].Value!;
        Assert.Equal(25.0, ageNumeric.Min);
        Assert.Equal(30.0, ageNumeric.Max);
    }

    [Fact]
    public void AddRow_WithNulls_CountsNullsProperly()
    {
        StatisticsCollector collector = new();

        collector.AddRow(CreateRow(("name", DataValue.FromString("Alice", _arena))), _arena);
        collector.AddRow(CreateRow(("name", DataValue.Null(DataKind.String))), _arena);
        collector.AddRow(CreateRow(("name", DataValue.FromString("Charlie", _arena))), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        CountResult count = (CountResult)stats["name"].Results["count"].Value!;

        Assert.Equal(2, count.NonNull);
        Assert.Equal(1, count.NullOrEmpty);
    }

    [Fact]
    public void GetStatistics_Empty_ReturnsEmptyDictionary()
    {
        StatisticsCollector collector = new();

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();

        Assert.Empty(stats);
    }

    [Fact]
    public void AddRow_CustomTopK_RespectsLimit()
    {
        StatisticsCollector collector = new(topK: 3);

        // Add many distinct values
        for (int i = 0; i < 100; i++)
        {
            collector.AddRow(CreateRow(("category", DataValue.FromString($"cat_{i % 5}", _arena))), _arena);
        }

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        TopKResult topK = (TopKResult)stats["category"].Results["top_k"].Value!;

        Assert.True(topK.Entries.Count <= 3);
    }

    [Fact]
    public void AddRow_UInt8Column_HasNumericStatistics()
    {
        StatisticsCollector collector = new();

        collector.AddRow(CreateRow(("byte_val", DataValue.FromUInt8(10))), _arena);
        collector.AddRow(CreateRow(("byte_val", DataValue.FromUInt8(200))), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();

        Assert.Contains("numeric", stats["byte_val"].Results.Keys);
        NumericResult numeric = (NumericResult)stats["byte_val"].Results["numeric"].Value!;
        Assert.Equal(10.0, numeric.Min);
        Assert.Equal(200.0, numeric.Max);
    }

    [Fact]
    public void ColumnStatistics_HasCorrectColumnName()
    {
        StatisticsCollector collector = new();

        collector.AddRow(CreateRow(("my_column", DataValue.FromFloat32(1.0f))), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Assert.Equal("my_column", stats["my_column"].ColumnName);
    }

    [Fact]
    public void AddRow_ImageColumn_CollectsImageStats()
    {
        StatisticsCollector collector = new();

        // Minimal JPEG: SOI + SOF0
        byte[] jpeg = new byte[20];
        jpeg[0] = 0xFF;
        jpeg[1] = 0xD8;
        jpeg[2] = 0xFF;
        jpeg[3] = 0xC0;
        jpeg[4] = 0x00;
        jpeg[5] = 0x0B;
        jpeg[6] = 0x08;
        jpeg[7] = 0x01; jpeg[8] = 0xE0; // height 480
        jpeg[9] = 0x02; jpeg[10] = 0x80; // width 640
        jpeg[11] = 0x03; // 3 channels

        collector.AddRow(CreateRow(("img", DataValue.FromImage(jpeg, _arena))), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Assert.Contains("image_stats", stats["img"].Results.Keys);

        ImageStatsResult imageResult = (ImageStatsResult)stats["img"].Results["image_stats"].Value!;
        Assert.Equal(1, imageResult.ImageCount);
        Assert.NotNull(imageResult.AspectRatioHistogram);
    }

    [Theory]
    [InlineData(DataKind.Image)]
    [InlineData(DataKind.UInt8Array)]
    [InlineData(DataKind.Vector)]
    [InlineData(DataKind.Matrix)]
    [InlineData(DataKind.Tensor)]
    public void AddRow_BinaryOrArrayKind_OmitsTopK(DataKind kind)
    {
        StatisticsCollector collector = new();

        DataValue value = kind switch
        {
            DataKind.Image => DataValue.FromImage(new byte[] { 0xFF, 0xD8, 0xFF, 0xC0 }, _arena),
            DataKind.UInt8Array => DataValue.FromByteArray(new byte[] { 1, 2, 3 }, _arena),
            DataKind.Vector => DataValue.FromVector(new float[] { 1.0f, 2.0f }, _arena),
            DataKind.Matrix => DataValue.FromMatrix(new float[] { 1.0f, 2.0f }, 1, 2, _arena),
            DataKind.Tensor => DataValue.FromTensor(new float[] { 1.0f }, new int[] { 1 }, _arena),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        collector.AddRow(CreateRow(("data", value)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Assert.DoesNotContain("top_k", stats["data"].Results.Keys);
    }

    private static Row CreateRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = new string[columns.Length];
        DataValue[] values = new DataValue[columns.Length];

        for (int i = 0; i < columns.Length; i++)
        {
            names[i] = columns[i].Name;
            values[i] = columns[i].Value;
        }

        return new Row(names, values);
    }
}
