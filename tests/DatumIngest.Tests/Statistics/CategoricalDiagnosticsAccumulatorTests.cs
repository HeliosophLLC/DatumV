namespace DatumIngest.Tests.Statistics;

using DatumIngest.Model;
using DatumIngest.Statistics;
using DatumIngest.Statistics.Accumulators;

/// <summary>
/// Tests for <see cref="CategoricalDiagnosticsAccumulator"/>.
/// </summary>
public sealed class CategoricalDiagnosticsAccumulatorTests : IDisposable
{
    private readonly Arena _arena = new();

    public void Dispose() => _arena.Dispose();

    [Fact]
    public void GetResult_Empty_ReturnsZeros()
    {
        CategoricalDiagnosticsAccumulator accumulator = new(10);

        CategoricalDiagnosticsResult result = (CategoricalDiagnosticsResult)accumulator.GetResult().Value!;

        Assert.Equal(0.0, result.CoverageTopK);
        Assert.Equal(0.0, result.RareRatio);
        Assert.Equal(0, result.RareCategoryCount);
        Assert.Equal(0, result.TotalCategoryCount);
    }

    [Fact]
    public void GetResult_HasCorrectName()
    {
        CategoricalDiagnosticsAccumulator accumulator = new(10);

        Assert.Equal("categorical_diagnostics", accumulator.GetResult().Name);
    }

    [Fact]
    public void GetResult_UniformDistribution_CoverageEqualsKOverN()
    {
        CategoricalDiagnosticsAccumulator accumulator = new(3);

        // 6 categories, each appearing 10 times — uniform distribution
        for (int category = 0; category < 6; category++)
        {
            for (int i = 0; i < 10; i++)
            {
                accumulator.Add(DataValue.FromString($"category_{category}", _arena), _arena);
            }
        }

        CategoricalDiagnosticsResult result = (CategoricalDiagnosticsResult)accumulator.GetResult().Value!;

        // Top 3 of 6 uniform categories → coverage = 30 / 60 = 0.5
        Assert.Equal(0.5, result.CoverageTopK, 0.001);
        Assert.Equal(0.0, result.RareRatio);
        Assert.Equal(6, result.TotalCategoryCount);
    }

    [Fact]
    public void GetResult_SkewedDistribution_HighCoverage()
    {
        CategoricalDiagnosticsAccumulator accumulator = new(2);

        // Two dominant categories
        for (int i = 0; i < 500; i++) accumulator.Add(DataValue.FromString("dominant_a", _arena), _arena);
        for (int i = 0; i < 480; i++) accumulator.Add(DataValue.FromString("dominant_b", _arena), _arena);
        // Many rare categories
        for (int i = 0; i < 20; i++) accumulator.Add(DataValue.FromString($"rare_{i}", _arena), _arena);

        CategoricalDiagnosticsResult result = (CategoricalDiagnosticsResult)accumulator.GetResult().Value!;

        // Top-2 coverage: (500 + 480) / 1000 = 0.98
        Assert.Equal(0.98, result.CoverageTopK, 0.001);
    }

    [Fact]
    public void GetResult_AllRare_RareRatioIsOne()
    {
        CategoricalDiagnosticsAccumulator accumulator = new(10);

        // Each category appears only once (count = 1, which is < 5)
        for (int i = 0; i < 20; i++)
        {
            accumulator.Add(DataValue.FromString($"singleton_{i}", _arena), _arena);
        }

        CategoricalDiagnosticsResult result = (CategoricalDiagnosticsResult)accumulator.GetResult().Value!;

        Assert.Equal(1.0, result.RareRatio);
        Assert.Equal(20, result.RareCategoryCount);
        Assert.Equal(20, result.TotalCategoryCount);
    }

    [Fact]
    public void GetResult_NoRare_RareRatioIsZero()
    {
        CategoricalDiagnosticsAccumulator accumulator = new(10);

        // Each category appears 5 times (count = 5 is NOT rare, since threshold is < 5)
        for (int category = 0; category < 4; category++)
        {
            for (int i = 0; i < 5; i++)
            {
                accumulator.Add(DataValue.FromString($"category_{category}", _arena), _arena);
            }
        }

        CategoricalDiagnosticsResult result = (CategoricalDiagnosticsResult)accumulator.GetResult().Value!;

        Assert.Equal(0.0, result.RareRatio);
        Assert.Equal(0, result.RareCategoryCount);
    }

    [Fact]
    public void GetResult_BoundaryThreshold_CountFiveIsNotRare_CountFourIsRare()
    {
        CategoricalDiagnosticsAccumulator accumulator = new(10);

        // Exactly 5 occurrences — NOT rare
        for (int i = 0; i < 5; i++) accumulator.Add(DataValue.FromString("five", _arena), _arena);

        // Exactly 4 occurrences — IS rare
        for (int i = 0; i < 4; i++) accumulator.Add(DataValue.FromString("four", _arena), _arena);

        CategoricalDiagnosticsResult result = (CategoricalDiagnosticsResult)accumulator.GetResult().Value!;

        Assert.Equal(1, result.RareCategoryCount);
        Assert.Equal(2, result.TotalCategoryCount);
        Assert.Equal(0.5, result.RareRatio, 0.001);
    }

    [Fact]
    public void Add_NullValues_AreExcluded()
    {
        CategoricalDiagnosticsAccumulator accumulator = new(10);

        accumulator.Add(DataValue.Null(DataKind.String), _arena);
        accumulator.Add(DataValue.Null(DataKind.String), _arena);
        accumulator.Add(DataValue.FromString("only_value", _arena), _arena);

        CategoricalDiagnosticsResult result = (CategoricalDiagnosticsResult)accumulator.GetResult().Value!;

        // Only 1 non-null value counted
        Assert.Equal(1.0, result.CoverageTopK);
        Assert.Equal(1, result.TotalCategoryCount);
    }

    [Fact]
    public void GetResult_SingleCategory_FullCoverage()
    {
        CategoricalDiagnosticsAccumulator accumulator = new(10);

        for (int i = 0; i < 100; i++)
        {
            accumulator.Add(DataValue.FromString("only", _arena), _arena);
        }

        CategoricalDiagnosticsResult result = (CategoricalDiagnosticsResult)accumulator.GetResult().Value!;

        Assert.Equal(1.0, result.CoverageTopK);
        Assert.Equal(0.0, result.RareRatio);
        Assert.Equal(1, result.TotalCategoryCount);
    }

    [Fact]
    public void Add_NumericValues_ConvertsToString()
    {
        CategoricalDiagnosticsAccumulator accumulator = new(10);

        for (int i = 0; i < 10; i++) accumulator.Add(DataValue.FromFloat32(1.0f), _arena);
        for (int i = 0; i < 5; i++) accumulator.Add(DataValue.FromFloat32(2.0f), _arena);

        CategoricalDiagnosticsResult result = (CategoricalDiagnosticsResult)accumulator.GetResult().Value!;

        Assert.Equal(2, result.TotalCategoryCount);
        Assert.Equal(0.0, result.RareRatio);
    }

    [Fact]
    public void Add_BeyondMaxDistinctValues_CapsFrequencyMap()
    {
        CategoricalDiagnosticsAccumulator accumulator = new(10);

        // Add MaxDistinctValues + 100 distinct categories.
        int totalDistinct = CategoricalDiagnosticsAccumulator.MaxDistinctValues + 100;

        for (int i = 0; i < totalDistinct; i++)
        {
            accumulator.Add(DataValue.FromString($"cat_{i}", _arena), _arena);
        }

        CategoricalDiagnosticsResult result = (CategoricalDiagnosticsResult)accumulator.GetResult().Value!;

        // The frequency map should be capped at MaxDistinctValues.
        Assert.Equal(CategoricalDiagnosticsAccumulator.MaxDistinctValues, result.TotalCategoryCount);
        Assert.True(result.Approximate);
    }

    [Fact]
    public void Add_ExistingKeysStillIncrementedAfterCap()
    {
        CategoricalDiagnosticsAccumulator accumulator = new(10);

        // Fill to cap with distinct values, but add a known key many times first.
        for (int i = 0; i < 100; i++)
        {
            accumulator.Add(DataValue.FromString("tracked", _arena), _arena);
        }

        for (int i = 0; i < CategoricalDiagnosticsAccumulator.MaxDistinctValues; i++)
        {
            accumulator.Add(DataValue.FromString($"fill_{i}", _arena), _arena);
        }

        // After capping, existing key should still be tracked.
        accumulator.Add(DataValue.FromString("tracked", _arena), _arena);

        CategoricalDiagnosticsResult result = (CategoricalDiagnosticsResult)accumulator.GetResult().Value!;

        Assert.True(result.Approximate);
        // "tracked" should be in top-K with 101 occurrences, making coverage > 0
        Assert.True(result.CoverageTopK > 0);
    }

    [Fact]
    public void GetResult_BelowCap_NotApproximate()
    {
        CategoricalDiagnosticsAccumulator accumulator = new(10);

        for (int i = 0; i < 100; i++)
        {
            accumulator.Add(DataValue.FromString($"cat_{i}", _arena), _arena);
        }

        CategoricalDiagnosticsResult result = (CategoricalDiagnosticsResult)accumulator.GetResult().Value!;

        Assert.False(result.Approximate);
        Assert.Equal(100, result.TotalCategoryCount);
    }

}
