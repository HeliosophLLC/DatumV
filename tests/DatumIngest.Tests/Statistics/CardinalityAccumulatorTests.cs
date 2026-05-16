namespace DatumIngest.Tests.Statistics;

using System.Linq;
using DatumIngest.Model;
using DatumIngest.Statistics.Accumulators;

public sealed class CardinalityAccumulatorTests : ServiceTestBase
{
    private readonly Arena _arena;

    public CardinalityAccumulatorTests()
    {
        _arena = CreateArena();
    }
    
    public override void Dispose()
    {
        _arena.Dispose();
        base.Dispose();
    }

    [Fact]
    public void Add_DistinctValues_EstimatesCardinality()
    {
        CardinalityAccumulator accumulator = new();

        for (int i = 0; i < 100; i++)
        {
            accumulator.Add(DataValue.FromString($"value_{i}", _arena), _arena);
        }

        CardinalityResult result = (CardinalityResult)accumulator.GetResults().Single().Value!;
        // HyperLogLog is approximate; allow 10% tolerance for small sets
        Assert.InRange(result.EstimatedDistinctCount, 85, 115);
    }

    [Fact]
    public void Add_DuplicateValues_EstimatesCorrectly()
    {
        CardinalityAccumulator accumulator = new();

        for (int i = 0; i < 1000; i++)
        {
            accumulator.Add(DataValue.FromString($"value_{i % 10}", _arena), _arena);
        }

        CardinalityResult result = (CardinalityResult)accumulator.GetResults().Single().Value!;
        Assert.InRange(result.EstimatedDistinctCount, 8, 12);
    }

    [Fact]
    public void Add_NumericValues_TracksDistinct()
    {
        CardinalityAccumulator accumulator = new();

        accumulator.Add(DataValue.FromFloat32(1.0f), _arena);
        accumulator.Add(DataValue.FromFloat32(2.0f), _arena);
        accumulator.Add(DataValue.FromFloat32(1.0f), _arena);
        accumulator.Add(DataValue.FromFloat32(3.0f), _arena);

        CardinalityResult result = (CardinalityResult)accumulator.GetResults().Single().Value!;
        Assert.InRange(result.EstimatedDistinctCount, 2, 4);
    }

    [Fact]
    public void Add_NullValues_AreSkipped()
    {
        CardinalityAccumulator accumulator = new();

        accumulator.Add(DataValue.Null(DataKind.String), _arena);
        accumulator.Add(DataValue.FromString("a", _arena), _arena);
        accumulator.Add(DataValue.Null(DataKind.String), _arena);

        CardinalityResult result = (CardinalityResult)accumulator.GetResults().Single().Value!;
        Assert.InRange(result.EstimatedDistinctCount, 1, 2);
    }

    [Fact]
    public void Add_LargeDataset_WithinAcceptableError()
    {
        CardinalityAccumulator accumulator = new();

        int distinctCount = 5000;
        for (int i = 0; i < distinctCount; i++)
        {
            accumulator.Add(DataValue.FromString($"item_{i}", _arena), _arena);
        }

        CardinalityResult result = (CardinalityResult)accumulator.GetResults().Single().Value!;
        // HyperLogLog should be within ~2% for larger datasets
        double errorPercent = Math.Abs(result.EstimatedDistinctCount - distinctCount) * 100.0 / distinctCount;
        Assert.True(errorPercent < 5.0, $"Error was {errorPercent:F2}% for {distinctCount} distinct values");
    }

    [Fact]
    public void Add_Empty_ReturnsZero()
    {
        CardinalityAccumulator accumulator = new();

        CardinalityResult result = (CardinalityResult)accumulator.GetResults().Single().Value!;
        Assert.Equal(0, result.EstimatedDistinctCount);
    }

    [Fact]
    public void GetResult_HasCorrectName()
    {
        CardinalityAccumulator accumulator = new();
        Assert.Equal("cardinality", accumulator.GetResults().Single().Name);
    }
}
