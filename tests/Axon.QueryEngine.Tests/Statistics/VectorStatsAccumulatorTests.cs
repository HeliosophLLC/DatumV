namespace Axon.QueryEngine.Tests.Statistics;

using Axon.QueryEngine.Model;
using Axon.QueryEngine.Statistics;
using Axon.QueryEngine.Statistics.Accumulators;

public sealed class VectorStatsAccumulatorTests
{
    [Fact]
    public void Add_SingleVector_TracksElementStats()
    {
        VectorStatsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromVector([1.0f, 2.0f, 3.0f]));

        VectorStatsResult result = (VectorStatsResult)accumulator.GetResult().Value!;
        Assert.Equal(1, result.ValueCount);
        Assert.Equal(3, result.MinElementCount);
        Assert.Equal(3, result.MaxElementCount);
        Assert.Equal(1, result.MinRank);
        Assert.Equal(1, result.MaxRank);
        Assert.Equal(3, result.ElementStats.Count);
        Assert.Equal(1.0, result.ElementStats.Min);
        Assert.Equal(3.0, result.ElementStats.Max);
        Assert.Equal(2.0, result.ElementStats.Mean, 1e-10);
    }

    [Fact]
    public void Add_MultipleVectors_AggregatesElementStats()
    {
        VectorStatsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromVector([1.0f, 2.0f]));
        accumulator.Add(DataValue.FromVector([10.0f, 20.0f, 30.0f]));

        VectorStatsResult result = (VectorStatsResult)accumulator.GetResult().Value!;
        Assert.Equal(2, result.ValueCount);
        Assert.Equal(2, result.MinElementCount);
        Assert.Equal(3, result.MaxElementCount);
        Assert.Equal(5, result.ElementStats.Count);
        Assert.Equal(1.0, result.ElementStats.Min);
        Assert.Equal(30.0, result.ElementStats.Max);
    }

    [Fact]
    public void Add_Matrix_TracksRankAndElements()
    {
        VectorStatsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromMatrix([1.0f, 2.0f, 3.0f, 4.0f], 2, 2));

        VectorStatsResult result = (VectorStatsResult)accumulator.GetResult().Value!;
        Assert.Equal(1, result.ValueCount);
        Assert.Equal(2, result.MinRank);
        Assert.Equal(2, result.MaxRank);
        Assert.Equal(4, result.MinElementCount);
        Assert.Equal(4, result.MaxElementCount);
        Assert.Equal(4, result.ElementStats.Count);
        Assert.Equal(1.0, result.ElementStats.Min);
        Assert.Equal(4.0, result.ElementStats.Max);
        Assert.Equal(2.5, result.ElementStats.Mean, 1e-10);
    }

    [Fact]
    public void Add_Tensor_TracksArbitraryRank()
    {
        VectorStatsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromTensor([1.0f, 2.0f, 3.0f, 4.0f, 5.0f, 6.0f, 7.0f, 8.0f], [2, 2, 2]));

        VectorStatsResult result = (VectorStatsResult)accumulator.GetResult().Value!;
        Assert.Equal(3, result.MinRank);
        Assert.Equal(3, result.MaxRank);
        Assert.Equal(8, result.ElementStats.Count);
    }

    [Fact]
    public void Add_MixedRanks_TracksMinMaxRank()
    {
        VectorStatsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromVector([1.0f, 2.0f]));
        accumulator.Add(DataValue.FromMatrix([1.0f, 2.0f, 3.0f, 4.0f], 2, 2));

        VectorStatsResult result = (VectorStatsResult)accumulator.GetResult().Value!;
        Assert.Equal(1, result.MinRank);
        Assert.Equal(2, result.MaxRank);
    }

    [Fact]
    public void Add_NullValues_Ignored()
    {
        VectorStatsAccumulator accumulator = new();

        accumulator.Add(DataValue.Null(DataKind.Vector));
        accumulator.Add(DataValue.FromVector([5.0f]));

        VectorStatsResult result = (VectorStatsResult)accumulator.GetResult().Value!;
        Assert.Equal(1, result.ValueCount);
    }

    [Fact]
    public void Add_NoValues_ReturnsZeros()
    {
        VectorStatsAccumulator accumulator = new();

        VectorStatsResult result = (VectorStatsResult)accumulator.GetResult().Value!;
        Assert.Equal(0, result.ValueCount);
        Assert.Equal(0, result.MinElementCount);
        Assert.Equal(0, result.MaxElementCount);
    }

    [Fact]
    public void Merge_TwoAccumulators_CombinesCorrectly()
    {
        VectorStatsAccumulator first = new();
        VectorStatsAccumulator second = new();

        first.Add(DataValue.FromVector([1.0f, 2.0f]));
        second.Add(DataValue.FromVector([10.0f, 20.0f, 30.0f]));

        first.Merge(second);

        VectorStatsResult result = (VectorStatsResult)first.GetResult().Value!;
        Assert.Equal(2, result.ValueCount);
        Assert.Equal(2, result.MinElementCount);
        Assert.Equal(3, result.MaxElementCount);
        Assert.Equal(5, result.ElementStats.Count);
        Assert.Equal(1.0, result.ElementStats.Min);
        Assert.Equal(30.0, result.ElementStats.Max);
    }

    [Fact]
    public void GetResult_HasCorrectName()
    {
        VectorStatsAccumulator accumulator = new();
        Assert.Equal("vector_stats", accumulator.GetResult().Name);
    }
}
