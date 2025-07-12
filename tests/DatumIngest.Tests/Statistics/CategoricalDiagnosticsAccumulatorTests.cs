namespace DatumIngest.Tests.Statistics;

using DatumIngest.Model;
using DatumIngest.Statistics;
using DatumIngest.Statistics.Accumulators;

/// <summary>
/// Tests for <see cref="CategoricalDiagnosticsAccumulator"/>.
/// </summary>
public sealed class CategoricalDiagnosticsAccumulatorTests
{
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
                accumulator.Add(DataValue.FromString($"category_{category}"));
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
        for (int i = 0; i < 500; i++) accumulator.Add(DataValue.FromString("dominant_a"));
        for (int i = 0; i < 480; i++) accumulator.Add(DataValue.FromString("dominant_b"));
        // Many rare categories
        for (int i = 0; i < 20; i++) accumulator.Add(DataValue.FromString($"rare_{i}"));

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
            accumulator.Add(DataValue.FromString($"singleton_{i}"));
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
                accumulator.Add(DataValue.FromString($"category_{category}"));
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
        for (int i = 0; i < 5; i++) accumulator.Add(DataValue.FromString("five"));

        // Exactly 4 occurrences — IS rare
        for (int i = 0; i < 4; i++) accumulator.Add(DataValue.FromString("four"));

        CategoricalDiagnosticsResult result = (CategoricalDiagnosticsResult)accumulator.GetResult().Value!;

        Assert.Equal(1, result.RareCategoryCount);
        Assert.Equal(2, result.TotalCategoryCount);
        Assert.Equal(0.5, result.RareRatio, 0.001);
    }

    [Fact]
    public void Add_NullValues_AreExcluded()
    {
        CategoricalDiagnosticsAccumulator accumulator = new(10);

        accumulator.Add(DataValue.Null(DataKind.String));
        accumulator.Add(DataValue.Null(DataKind.String));
        accumulator.Add(DataValue.FromString("only_value"));

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
            accumulator.Add(DataValue.FromString("only"));
        }

        CategoricalDiagnosticsResult result = (CategoricalDiagnosticsResult)accumulator.GetResult().Value!;

        Assert.Equal(1.0, result.CoverageTopK);
        Assert.Equal(0.0, result.RareRatio);
        Assert.Equal(1, result.TotalCategoryCount);
    }

    [Fact]
    public void Merge_CombinesFrequenciesAndTotals()
    {
        CategoricalDiagnosticsAccumulator first = new(10);
        for (int i = 0; i < 10; i++) first.Add(DataValue.FromString("a"));
        for (int i = 0; i < 3; i++) first.Add(DataValue.FromString("b"));

        CategoricalDiagnosticsAccumulator second = new(10);
        for (int i = 0; i < 5; i++) second.Add(DataValue.FromString("a"));
        for (int i = 0; i < 2; i++) second.Add(DataValue.FromString("c"));

        first.Merge(second);

        CategoricalDiagnosticsResult result = (CategoricalDiagnosticsResult)first.GetResult().Value!;

        // a=15, b=3, c=2 → total=20, 3 categories
        Assert.Equal(3, result.TotalCategoryCount);
        // b(3) and c(2) are rare (< 5)
        Assert.Equal(2, result.RareCategoryCount);
        // coverage top-10 covers all 3 categories → (15+3+2)/20 = 1.0
        Assert.Equal(1.0, result.CoverageTopK);
    }

    [Fact]
    public void Add_NumericValues_ConvertsToString()
    {
        CategoricalDiagnosticsAccumulator accumulator = new(10);

        for (int i = 0; i < 10; i++) accumulator.Add(DataValue.FromScalar(1.0f));
        for (int i = 0; i < 5; i++) accumulator.Add(DataValue.FromScalar(2.0f));

        CategoricalDiagnosticsResult result = (CategoricalDiagnosticsResult)accumulator.GetResult().Value!;

        Assert.Equal(2, result.TotalCategoryCount);
        Assert.Equal(0.0, result.RareRatio);
    }
}
