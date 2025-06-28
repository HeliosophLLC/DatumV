namespace Axon.QueryEngine.Tests.Statistics;

using Axon.QueryEngine.Model;
using Axon.QueryEngine.Statistics;
using Axon.QueryEngine.Statistics.Accumulators;

public sealed class StatisticsCollectorTests
{
    [Fact]
    public void AddRow_NumericColumn_CollectsAllStatistics()
    {
        StatisticsCollector collector = new();

        collector.AddRow(CreateRow(("value", DataValue.FromScalar(1.0f))));
        collector.AddRow(CreateRow(("value", DataValue.FromScalar(2.0f))));
        collector.AddRow(CreateRow(("value", DataValue.FromScalar(3.0f))));

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

        collector.AddRow(CreateRow(("name", DataValue.FromString("Alice"))));
        collector.AddRow(CreateRow(("name", DataValue.FromString("Bob"))));

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

        collector.AddRow(CreateRow(("value", DataValue.FromScalar(1.0f))));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        ColumnStatistics columnStats = stats["value"];

        Assert.DoesNotContain("string_length", columnStats.Results.Keys);
    }

    [Fact]
    public void AddRow_StringColumn_DoesNotCollectNumeric()
    {
        StatisticsCollector collector = new();

        collector.AddRow(CreateRow(("name", DataValue.FromString("Alice"))));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        ColumnStatistics columnStats = stats["name"];

        Assert.DoesNotContain("numeric", columnStats.Results.Keys);
    }

    [Fact]
    public void AddRow_MultipleColumns_TracksEachIndependently()
    {
        StatisticsCollector collector = new();

        collector.AddRow(CreateRow(
            ("name", DataValue.FromString("Alice")),
            ("age", DataValue.FromScalar(30.0f))));
        collector.AddRow(CreateRow(
            ("name", DataValue.FromString("Bob")),
            ("age", DataValue.FromScalar(25.0f))));

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

        collector.AddRow(CreateRow(("name", DataValue.FromString("Alice"))));
        collector.AddRow(CreateRow(("name", DataValue.Null(DataKind.String))));
        collector.AddRow(CreateRow(("name", DataValue.FromString("Charlie"))));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        CountResult count = (CountResult)stats["name"].Results["count"].Value!;

        Assert.Equal(2, count.NonNull);
        Assert.Equal(1, count.NullOrEmpty);
    }

    [Fact]
    public void Merge_CombinesCollectors()
    {
        StatisticsCollector first = new();
        first.AddRow(CreateRow(("value", DataValue.FromScalar(1.0f))));
        first.AddRow(CreateRow(("value", DataValue.FromScalar(2.0f))));

        StatisticsCollector second = new();
        second.AddRow(CreateRow(("value", DataValue.FromScalar(3.0f))));
        second.AddRow(CreateRow(("value", DataValue.FromScalar(4.0f))));

        first.Merge(second);

        IReadOnlyDictionary<string, ColumnStatistics> stats = first.GetStatistics();
        CountResult count = (CountResult)stats["value"].Results["count"].Value!;
        Assert.Equal(4, count.NonNull);

        NumericResult numeric = (NumericResult)stats["value"].Results["numeric"].Value!;
        Assert.Equal(1.0, numeric.Min);
        Assert.Equal(4.0, numeric.Max);
        Assert.Equal(2.5, numeric.Mean, 1e-10);
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
            collector.AddRow(CreateRow(("category", DataValue.FromString($"cat_{i % 5}"))));
        }

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        TopKResult topK = (TopKResult)stats["category"].Results["top_k"].Value!;

        Assert.True(topK.Entries.Count <= 3);
    }

    [Fact]
    public void AddRow_UInt8Column_HasNumericStatistics()
    {
        StatisticsCollector collector = new();

        collector.AddRow(CreateRow(("byte_val", DataValue.FromUInt8(10))));
        collector.AddRow(CreateRow(("byte_val", DataValue.FromUInt8(200))));

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

        collector.AddRow(CreateRow(("my_column", DataValue.FromScalar(1.0f))));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Assert.Equal("my_column", stats["my_column"].ColumnName);
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
