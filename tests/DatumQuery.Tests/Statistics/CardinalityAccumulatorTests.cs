namespace Axon.QueryEngine.Tests.Statistics;

using Axon.QueryEngine.Model;
using Axon.QueryEngine.Statistics;
using Axon.QueryEngine.Statistics.Accumulators;

public sealed class CardinalityAccumulatorTests
{
    [Fact]
    public void Add_DistinctValues_EstimatesCardinality()
    {
        CardinalityAccumulator accumulator = new();

        for (int i = 0; i < 100; i++)
        {
            accumulator.Add(DataValue.FromString($"value_{i}"));
        }

        CardinalityResult result = (CardinalityResult)accumulator.GetResult().Value!;
        // HyperLogLog is approximate; allow 10% tolerance for small sets
        Assert.InRange(result.EstimatedDistinctCount, 85, 115);
    }

    [Fact]
    public void Add_DuplicateValues_EstimatesCorrectly()
    {
        CardinalityAccumulator accumulator = new();

        for (int i = 0; i < 1000; i++)
        {
            accumulator.Add(DataValue.FromString($"value_{i % 10}"));
        }

        CardinalityResult result = (CardinalityResult)accumulator.GetResult().Value!;
        Assert.InRange(result.EstimatedDistinctCount, 8, 12);
    }

    [Fact]
    public void Add_NumericValues_TracksDistinct()
    {
        CardinalityAccumulator accumulator = new();

        accumulator.Add(DataValue.FromScalar(1.0f));
        accumulator.Add(DataValue.FromScalar(2.0f));
        accumulator.Add(DataValue.FromScalar(1.0f));
        accumulator.Add(DataValue.FromScalar(3.0f));

        CardinalityResult result = (CardinalityResult)accumulator.GetResult().Value!;
        Assert.InRange(result.EstimatedDistinctCount, 2, 4);
    }

    [Fact]
    public void Add_NullValues_AreSkipped()
    {
        CardinalityAccumulator accumulator = new();

        accumulator.Add(DataValue.Null(DataKind.String));
        accumulator.Add(DataValue.FromString("a"));
        accumulator.Add(DataValue.Null(DataKind.String));

        CardinalityResult result = (CardinalityResult)accumulator.GetResult().Value!;
        Assert.InRange(result.EstimatedDistinctCount, 1, 2);
    }

    [Fact]
    public void Add_LargeDataset_WithinAcceptableError()
    {
        CardinalityAccumulator accumulator = new();

        int distinctCount = 5000;
        for (int i = 0; i < distinctCount; i++)
        {
            accumulator.Add(DataValue.FromString($"item_{i}"));
        }

        CardinalityResult result = (CardinalityResult)accumulator.GetResult().Value!;
        // HyperLogLog should be within ~2% for larger datasets
        double errorPercent = Math.Abs(result.EstimatedDistinctCount - distinctCount) * 100.0 / distinctCount;
        Assert.True(errorPercent < 5.0, $"Error was {errorPercent:F2}% for {distinctCount} distinct values");
    }

    [Fact]
    public void Merge_CombinesEstimates()
    {
        CardinalityAccumulator first = new();
        for (int i = 0; i < 50; i++)
        {
            first.Add(DataValue.FromString($"value_{i}"));
        }

        CardinalityAccumulator second = new();
        for (int i = 50; i < 100; i++)
        {
            second.Add(DataValue.FromString($"value_{i}"));
        }

        first.Merge(second);

        CardinalityResult result = (CardinalityResult)first.GetResult().Value!;
        Assert.InRange(result.EstimatedDistinctCount, 85, 115);
    }

    [Fact]
    public void Merge_WithOverlappingValues_DoesNotDoubleCount()
    {
        CardinalityAccumulator first = new();
        for (int i = 0; i < 50; i++)
        {
            first.Add(DataValue.FromString($"value_{i}"));
        }

        CardinalityAccumulator second = new();
        for (int i = 25; i < 75; i++) // 25 values overlap
        {
            second.Add(DataValue.FromString($"value_{i}"));
        }

        first.Merge(second);

        CardinalityResult result = (CardinalityResult)first.GetResult().Value!;
        // Should be ~75 distinct, not ~100
        Assert.InRange(result.EstimatedDistinctCount, 60, 90);
    }

    [Fact]
    public void Add_Empty_ReturnsZero()
    {
        CardinalityAccumulator accumulator = new();

        CardinalityResult result = (CardinalityResult)accumulator.GetResult().Value!;
        Assert.Equal(0, result.EstimatedDistinctCount);
    }

    [Fact]
    public void GetResult_HasCorrectName()
    {
        CardinalityAccumulator accumulator = new();
        Assert.Equal("cardinality", accumulator.GetResult().Name);
    }
}
