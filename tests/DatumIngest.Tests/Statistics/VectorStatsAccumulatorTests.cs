namespace DatumIngest.Tests.Statistics;

using DatumIngest.Model;
using DatumIngest.Statistics;
using DatumIngest.Statistics.Accumulators;

public sealed class VectorStatsAccumulatorTests : IDisposable
{
    private readonly Arena _arena = new();

    public void Dispose() => _arena.Dispose();

    [Fact]
    public void Add_SingleVector_TracksElementStats()
    {
        VectorStatsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromVector([1.0f, 2.0f, 3.0f], _arena), _arena);

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

        accumulator.Add(DataValue.FromVector([1.0f, 2.0f], _arena), _arena);
        accumulator.Add(DataValue.FromVector([10.0f, 20.0f, 30.0f], _arena), _arena);

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

        accumulator.Add(DataValue.FromMatrix([1.0f, 2.0f, 3.0f, 4.0f], 2, 2, _arena), _arena);

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

        accumulator.Add(DataValue.FromTensor([1.0f, 2.0f, 3.0f, 4.0f, 5.0f, 6.0f, 7.0f, 8.0f], [2, 2, 2], _arena), _arena);

        VectorStatsResult result = (VectorStatsResult)accumulator.GetResult().Value!;
        Assert.Equal(3, result.MinRank);
        Assert.Equal(3, result.MaxRank);
        Assert.Equal(8, result.ElementStats.Count);
    }

    [Fact]
    public void Add_MixedRanks_TracksMinMaxRank()
    {
        VectorStatsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromVector([1.0f, 2.0f], _arena), _arena);
        accumulator.Add(DataValue.FromMatrix([1.0f, 2.0f, 3.0f, 4.0f], 2, 2, _arena), _arena);

        VectorStatsResult result = (VectorStatsResult)accumulator.GetResult().Value!;
        Assert.Equal(1, result.MinRank);
        Assert.Equal(2, result.MaxRank);
    }

    [Fact]
    public void Add_NullValues_Ignored()
    {
        VectorStatsAccumulator accumulator = new();

        accumulator.Add(DataValue.Null(DataKind.Vector), _arena);
        accumulator.Add(DataValue.FromVector([5.0f], _arena), _arena);

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
    public void GetResult_HasCorrectName()
    {
        VectorStatsAccumulator accumulator = new();
        Assert.Equal("vector_stats", accumulator.GetResult().Name);
    }

    [Fact]
    public void Add_WithZeroElements_CountsZerosCorrectly()
    {
        VectorStatsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromVector([0.0f, 1.0f, 0.0f, 2.0f], _arena), _arena);

        VectorStatsResult result = (VectorStatsResult)accumulator.GetResult().Value!;
        Assert.Equal(2, result.ZeroElementCount);
        Assert.Equal(0.5, result.ZeroElementRatio, 1e-10);
        Assert.Equal(0, result.ZeroVectorCount);
    }

    [Fact]
    public void Add_AllZeroVector_DetectsZeroVector()
    {
        VectorStatsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromVector([0.0f, 0.0f, 0.0f], _arena), _arena);

        VectorStatsResult result = (VectorStatsResult)accumulator.GetResult().Value!;
        Assert.Equal(3, result.ZeroElementCount);
        Assert.Equal(1.0, result.ZeroElementRatio, 1e-10);
        Assert.Equal(1, result.ZeroVectorCount);
    }

    [Fact]
    public void Add_MixedZeroAndNonZeroVectors_TracksCorrectly()
    {
        VectorStatsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromVector([0.0f, 0.0f, 0.0f], _arena), _arena);
        accumulator.Add(DataValue.FromVector([0.0f, 0.2f, 0.0f], _arena), _arena);
        accumulator.Add(DataValue.FromVector([0.0f, 0.0f, 0.0f], _arena), _arena);

        VectorStatsResult result = (VectorStatsResult)accumulator.GetResult().Value!;
        Assert.Equal(8, result.ZeroElementCount);
        Assert.Equal(8.0 / 9.0, result.ZeroElementRatio, 1e-10);
        Assert.Equal(2, result.ZeroVectorCount);
    }

    [Fact]
    public void Add_NoZeroElements_ZeroCountsAreZero()
    {
        VectorStatsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromVector([1.0f, 2.0f, 3.0f], _arena), _arena);

        VectorStatsResult result = (VectorStatsResult)accumulator.GetResult().Value!;
        Assert.Equal(0, result.ZeroElementCount);
        Assert.Equal(0.0, result.ZeroElementRatio, 1e-10);
        Assert.Equal(0, result.ZeroVectorCount);
    }

    [Fact]
    public void Add_EmptyAccumulator_ZeroStatsAreZero()
    {
        VectorStatsAccumulator accumulator = new();

        VectorStatsResult result = (VectorStatsResult)accumulator.GetResult().Value!;
        Assert.Equal(0, result.ZeroElementCount);
        Assert.Equal(0.0, result.ZeroElementRatio, 1e-10);
        Assert.Equal(0, result.ZeroVectorCount);
    }

    [Fact]
    public void Add_Matrix_TracksZeroElements()
    {
        VectorStatsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromMatrix([0.0f, 0.0f, 0.0f, 0.0f], 2, 2, _arena), _arena);

        VectorStatsResult result = (VectorStatsResult)accumulator.GetResult().Value!;
        Assert.Equal(4, result.ZeroElementCount);
        Assert.Equal(1.0, result.ZeroElementRatio, 1e-10);
        Assert.Equal(1, result.ZeroVectorCount);
    }

    // --- L2 Norm Tests ---

    [Fact]
    public void Add_SingleVector_ComputesNormCorrectly()
    {
        VectorStatsAccumulator accumulator = new();

        // ||[3, 4]||₂ = 5.0
        accumulator.Add(DataValue.FromVector([3.0f, 4.0f], _arena), _arena);

        VectorStatsResult result = (VectorStatsResult)accumulator.GetResult().Value!;
        Assert.Equal(5.0, result.NormMin, 1e-10);
        Assert.Equal(5.0, result.NormMax, 1e-10);
        Assert.Equal(5.0, result.NormMean, 1e-10);
    }

    [Fact]
    public void Add_MultipleVectors_TracksNormMinMaxMean()
    {
        VectorStatsAccumulator accumulator = new();

        // ||[1, 0]||₂ = 1.0
        accumulator.Add(DataValue.FromVector([1.0f, 0.0f], _arena), _arena);
        // ||[3, 4]||₂ = 5.0
        accumulator.Add(DataValue.FromVector([3.0f, 4.0f], _arena), _arena);

        VectorStatsResult result = (VectorStatsResult)accumulator.GetResult().Value!;
        Assert.Equal(1.0, result.NormMin, 1e-10);
        Assert.Equal(5.0, result.NormMax, 1e-10);
        Assert.Equal(3.0, result.NormMean, 1e-10);
    }

    [Fact]
    public void Add_ZeroVector_NormIsZero()
    {
        VectorStatsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromVector([0.0f, 0.0f, 0.0f], _arena), _arena);

        VectorStatsResult result = (VectorStatsResult)accumulator.GetResult().Value!;
        Assert.Equal(0.0, result.NormMin, 1e-10);
        Assert.Equal(0.0, result.NormMax, 1e-10);
        Assert.Equal(0.0, result.NormMean, 1e-10);
    }

    [Fact]
    public void Add_EmptyAccumulator_NormsAreNaN()
    {
        VectorStatsAccumulator accumulator = new();

        VectorStatsResult result = (VectorStatsResult)accumulator.GetResult().Value!;
        Assert.True(double.IsNaN(result.NormMin));
        Assert.True(double.IsNaN(result.NormMax));
        Assert.True(double.IsNaN(result.NormMean));
    }

    [Fact]
    public void Add_NullValues_NormsUnaffected()
    {
        VectorStatsAccumulator accumulator = new();

        accumulator.Add(DataValue.Null(DataKind.Vector), _arena);
        // ||[3, 4]||₂ = 5.0
        accumulator.Add(DataValue.FromVector([3.0f, 4.0f], _arena), _arena);

        VectorStatsResult result = (VectorStatsResult)accumulator.GetResult().Value!;
        Assert.Equal(5.0, result.NormMin, 1e-10);
        Assert.Equal(5.0, result.NormMax, 1e-10);
        Assert.Equal(5.0, result.NormMean, 1e-10);
    }

    [Fact]
    public void Add_Matrix_ComputesNorm()
    {
        VectorStatsAccumulator accumulator = new();

        // ||[1, 2, 3, 4]||₂ = sqrt(1+4+9+16) = sqrt(30)
        accumulator.Add(DataValue.FromMatrix([1.0f, 2.0f, 3.0f, 4.0f], 2, 2, _arena), _arena);

        VectorStatsResult result = (VectorStatsResult)accumulator.GetResult().Value!;
        Assert.Equal(Math.Sqrt(30.0), result.NormMin, 1e-10);
        Assert.Equal(Math.Sqrt(30.0), result.NormMax, 1e-10);
        Assert.Equal(Math.Sqrt(30.0), result.NormMean, 1e-10);
    }

    [Fact]
    public void Add_Tensor_ComputesNorm()
    {
        VectorStatsAccumulator accumulator = new();

        // ||[1, 1, 1, 1, 1, 1, 1, 1]||₂ = sqrt(8)
        accumulator.Add(DataValue.FromTensor([1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f], [2, 2, 2], _arena), _arena);

        VectorStatsResult result = (VectorStatsResult)accumulator.GetResult().Value!;
        Assert.Equal(Math.Sqrt(8.0), result.NormMin, 1e-10);
        Assert.Equal(Math.Sqrt(8.0), result.NormMax, 1e-10);
        Assert.Equal(Math.Sqrt(8.0), result.NormMean, 1e-10);
    }

}
