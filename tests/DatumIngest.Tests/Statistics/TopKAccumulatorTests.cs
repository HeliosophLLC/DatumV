namespace DatumQuery.Tests.Statistics;

using DatumQuery.Model;
using DatumQuery.Statistics;
using DatumQuery.Statistics.Accumulators;

public sealed class TopKAccumulatorTests
{
    [Fact]
    public void Add_Values_TracksFrequencies()
    {
        TopKAccumulator accumulator = new(10);

        accumulator.Add(DataValue.FromString("a"));
        accumulator.Add(DataValue.FromString("b"));
        accumulator.Add(DataValue.FromString("a"));
        accumulator.Add(DataValue.FromString("c"));
        accumulator.Add(DataValue.FromString("a"));

        TopKResult result = (TopKResult)accumulator.GetResult().Value!;
        Assert.Equal(3, result.Entries.Count);
        Assert.Equal("a", result.Entries[0].Key);
        Assert.Equal(3, result.Entries[0].Value);
    }

    [Fact]
    public void Add_MoreThanK_KeepsTopK()
    {
        TopKAccumulator accumulator = new(3);

        // Add values with different frequencies
        for (int i = 0; i < 100; i++)
        {
            accumulator.Add(DataValue.FromString("high"));
        }
        for (int i = 0; i < 50; i++)
        {
            accumulator.Add(DataValue.FromString("medium"));
        }
        for (int i = 0; i < 25; i++)
        {
            accumulator.Add(DataValue.FromString("low"));
        }
        for (int i = 0; i < 1; i++)
        {
            accumulator.Add(DataValue.FromString("very_low_" + i));
        }

        TopKResult result = (TopKResult)accumulator.GetResult().Value!;
        Assert.True(result.Entries.Count <= 3);
        Assert.Equal("high", result.Entries[0].Key);
    }

    [Fact]
    public void Add_NumericValues_ConvertsToString()
    {
        TopKAccumulator accumulator = new(10);

        accumulator.Add(DataValue.FromScalar(1.0f));
        accumulator.Add(DataValue.FromScalar(1.0f));
        accumulator.Add(DataValue.FromScalar(2.0f));

        TopKResult result = (TopKResult)accumulator.GetResult().Value!;
        Assert.Equal(2, result.Entries.Count);
        Assert.Equal(2, result.Entries[0].Value); // 1.0 appeared twice
    }

    [Fact]
    public void Add_NullValues_AreSkipped()
    {
        TopKAccumulator accumulator = new(10);

        accumulator.Add(DataValue.Null(DataKind.String));
        accumulator.Add(DataValue.FromString("a"));

        TopKResult result = (TopKResult)accumulator.GetResult().Value!;
        Assert.Single(result.Entries);
    }

    [Fact]
    public void Merge_CombinesFrequencies()
    {
        TopKAccumulator first = new(10);
        first.Add(DataValue.FromString("a"));
        first.Add(DataValue.FromString("a"));
        first.Add(DataValue.FromString("b"));

        TopKAccumulator second = new(10);
        second.Add(DataValue.FromString("a"));
        second.Add(DataValue.FromString("c"));
        second.Add(DataValue.FromString("c"));

        first.Merge(second);

        TopKResult result = (TopKResult)first.GetResult().Value!;
        Assert.Equal(3, result.Entries.Count);
        Assert.Equal("a", result.Entries[0].Key);
        Assert.Equal(3, result.Entries[0].Value);
    }

    [Fact]
    public void Add_SortedByFrequencyDescending()
    {
        TopKAccumulator accumulator = new(10);

        for (int i = 0; i < 1; i++) accumulator.Add(DataValue.FromString("rare"));
        for (int i = 0; i < 5; i++) accumulator.Add(DataValue.FromString("common"));
        for (int i = 0; i < 3; i++) accumulator.Add(DataValue.FromString("medium"));

        TopKResult result = (TopKResult)accumulator.GetResult().Value!;
        Assert.Equal("common", result.Entries[0].Key);
        Assert.Equal("medium", result.Entries[1].Key);
        Assert.Equal("rare", result.Entries[2].Key);
    }

    [Fact]
    public void Add_Empty_ReturnsEmptyEntries()
    {
        TopKAccumulator accumulator = new(10);

        TopKResult result = (TopKResult)accumulator.GetResult().Value!;
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void GetResult_HasCorrectName()
    {
        TopKAccumulator accumulator = new(10);
        Assert.Equal("top_k", accumulator.GetResult().Name);
    }
}
