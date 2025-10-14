namespace DatumIngest.Tests.Statistics;

using DatumIngest.Model;
using DatumIngest.Statistics;
using DatumIngest.Statistics.Accumulators;

public sealed class TopKAccumulatorTests : IDisposable
{
    private readonly Arena _arena = new();

    public void Dispose() => _arena.Dispose();

    [Fact]
    public void Add_Values_TracksFrequencies()
    {
        TopKAccumulator accumulator = new(10);

        accumulator.Add(DataValue.FromString("a", _arena), _arena);
        accumulator.Add(DataValue.FromString("b", _arena), _arena);
        accumulator.Add(DataValue.FromString("a", _arena), _arena);
        accumulator.Add(DataValue.FromString("c", _arena), _arena);
        accumulator.Add(DataValue.FromString("a", _arena), _arena);

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
            accumulator.Add(DataValue.FromString("high", _arena), _arena);
        }
        for (int i = 0; i < 50; i++)
        {
            accumulator.Add(DataValue.FromString("medium", _arena), _arena);
        }
        for (int i = 0; i < 25; i++)
        {
            accumulator.Add(DataValue.FromString("low", _arena), _arena);
        }
        for (int i = 0; i < 1; i++)
        {
            accumulator.Add(DataValue.FromString("very_low_" + i, _arena), _arena);
        }

        TopKResult result = (TopKResult)accumulator.GetResult().Value!;
        Assert.True(result.Entries.Count <= 3);
        Assert.Equal("high", result.Entries[0].Key);
    }

    [Fact]
    public void Add_NumericValues_ConvertsToString()
    {
        TopKAccumulator accumulator = new(10);

        accumulator.Add(DataValue.FromFloat32(1.0f), _arena);
        accumulator.Add(DataValue.FromFloat32(1.0f), _arena);
        accumulator.Add(DataValue.FromFloat32(2.0f), _arena);

        TopKResult result = (TopKResult)accumulator.GetResult().Value!;
        Assert.Equal(2, result.Entries.Count);
        Assert.Equal(2, result.Entries[0].Value); // 1.0 appeared twice
    }

    [Fact]
    public void Add_NullValues_AreSkipped()
    {
        TopKAccumulator accumulator = new(10);

        accumulator.Add(DataValue.Null(DataKind.String), _arena);
        accumulator.Add(DataValue.FromString("a", _arena), _arena);

        TopKResult result = (TopKResult)accumulator.GetResult().Value!;
        Assert.Single(result.Entries);
    }

    [Fact]
    public void Add_SortedByFrequencyDescending()
    {
        TopKAccumulator accumulator = new(10);

        for (int i = 0; i < 1; i++) accumulator.Add(DataValue.FromString("rare", _arena), _arena);
        for (int i = 0; i < 5; i++) accumulator.Add(DataValue.FromString("common", _arena), _arena);
        for (int i = 0; i < 3; i++) accumulator.Add(DataValue.FromString("medium", _arena), _arena);

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
