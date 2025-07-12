namespace DatumIngest.Tests.Statistics;

using DatumIngest.Model;
using DatumIngest.Statistics;
using DatumIngest.Statistics.Accumulators;

public sealed class NumericAccumulatorTests
{
    [Fact]
    public void Add_SingleValue_CorrectMinMaxMean()
    {
        NumericAccumulator accumulator = new();

        accumulator.Add(DataValue.FromScalar(5.0f));

        NumericResult result = (NumericResult)accumulator.GetResult().Value!;
        Assert.Equal(1, result.Count);
        Assert.Equal(5.0, result.Min);
        Assert.Equal(5.0, result.Max);
        Assert.Equal(5.0, result.Mean);
        Assert.Equal(0.0, result.Variance);
    }

    [Fact]
    public void Add_MultipleValues_CorrectStatistics()
    {
        NumericAccumulator accumulator = new();

        accumulator.Add(DataValue.FromScalar(2.0f));
        accumulator.Add(DataValue.FromScalar(4.0f));
        accumulator.Add(DataValue.FromScalar(6.0f));
        accumulator.Add(DataValue.FromScalar(8.0f));

        NumericResult result = (NumericResult)accumulator.GetResult().Value!;
        Assert.Equal(4, result.Count);
        Assert.Equal(2.0, result.Min);
        Assert.Equal(8.0, result.Max);
        Assert.Equal(5.0, result.Mean, 1e-10);
        Assert.Equal(5.0, result.Variance, 1e-10); // Population variance of [2,4,6,8]
    }

    [Fact]
    public void Add_UInt8Values_TracksCorrectly()
    {
        NumericAccumulator accumulator = new();

        accumulator.Add(DataValue.FromUInt8(10));
        accumulator.Add(DataValue.FromUInt8(20));
        accumulator.Add(DataValue.FromUInt8(30));

        NumericResult result = (NumericResult)accumulator.GetResult().Value!;
        Assert.Equal(3, result.Count);
        Assert.Equal(10.0, result.Min);
        Assert.Equal(30.0, result.Max);
        Assert.Equal(20.0, result.Mean, 1e-10);
    }

    [Fact]
    public void Add_NullValues_AreSkipped()
    {
        NumericAccumulator accumulator = new();

        accumulator.Add(DataValue.Null(DataKind.Scalar));
        accumulator.Add(DataValue.FromScalar(5.0f));
        accumulator.Add(DataValue.Null(DataKind.Scalar));

        NumericResult result = (NumericResult)accumulator.GetResult().Value!;
        Assert.Equal(1, result.Count);
        Assert.Equal(5.0, result.Mean);
    }

    [Fact]
    public void Add_StringValues_AreSkipped()
    {
        NumericAccumulator accumulator = new();

        accumulator.Add(DataValue.FromString("hello"));
        accumulator.Add(DataValue.FromScalar(10.0f));

        NumericResult result = (NumericResult)accumulator.GetResult().Value!;
        Assert.Equal(1, result.Count);
        Assert.Equal(10.0, result.Mean);
    }

    [Fact]
    public void Add_NoValues_ReturnsNaN()
    {
        NumericAccumulator accumulator = new();

        NumericResult result = (NumericResult)accumulator.GetResult().Value!;
        Assert.Equal(0, result.Count);
        Assert.True(double.IsNaN(result.Min));
        Assert.True(double.IsNaN(result.Max));
        Assert.True(double.IsNaN(result.Mean));
    }

    [Fact]
    public void Add_WelfordsAlgorithm_MatchesNaiveCalculation()
    {
        NumericAccumulator accumulator = new();

        float[] values = [1.0f, 2.0f, 3.0f, 4.0f, 5.0f, 100.0f, 200.0f, 0.01f];

        foreach (float value in values)
        {
            accumulator.Add(DataValue.FromScalar(value));
        }

        // Naive calculation
        double sum = 0;
        foreach (float value in values)
        {
            sum += value;
        }
        double naiveMean = sum / values.Length;

        double sumSquaredDiff = 0;
        foreach (float value in values)
        {
            sumSquaredDiff += (value - naiveMean) * (value - naiveMean);
        }
        double naiveVariance = sumSquaredDiff / values.Length;

        NumericResult result = (NumericResult)accumulator.GetResult().Value!;
        Assert.Equal(naiveMean, result.Mean, 1e-6);
        Assert.Equal(naiveVariance, result.Variance, 1e-4);
    }

    [Fact]
    public void Merge_CombinesTwoAccumulators()
    {
        NumericAccumulator first = new();
        first.Add(DataValue.FromScalar(1.0f));
        first.Add(DataValue.FromScalar(2.0f));
        first.Add(DataValue.FromScalar(3.0f));

        NumericAccumulator second = new();
        second.Add(DataValue.FromScalar(4.0f));
        second.Add(DataValue.FromScalar(5.0f));

        first.Merge(second);

        NumericResult result = (NumericResult)first.GetResult().Value!;
        Assert.Equal(5, result.Count);
        Assert.Equal(1.0, result.Min);
        Assert.Equal(5.0, result.Max);
        Assert.Equal(3.0, result.Mean, 1e-10);
    }

    [Fact]
    public void Merge_PreservesVariance()
    {
        NumericAccumulator combined = new();
        foreach (float value in new[] { 1.0f, 2.0f, 3.0f, 4.0f, 5.0f })
        {
            combined.Add(DataValue.FromScalar(value));
        }

        NumericAccumulator first = new();
        first.Add(DataValue.FromScalar(1.0f));
        first.Add(DataValue.FromScalar(2.0f));

        NumericAccumulator second = new();
        second.Add(DataValue.FromScalar(3.0f));
        second.Add(DataValue.FromScalar(4.0f));
        second.Add(DataValue.FromScalar(5.0f));

        first.Merge(second);

        NumericResult combinedResult = (NumericResult)combined.GetResult().Value!;
        NumericResult mergedResult = (NumericResult)first.GetResult().Value!;

        Assert.Equal(combinedResult.Mean, mergedResult.Mean, 1e-10);
        Assert.Equal(combinedResult.Variance, mergedResult.Variance, 1e-10);
    }

    [Fact]
    public void Merge_WithEmptyAccumulator_NoChange()
    {
        NumericAccumulator first = new();
        first.Add(DataValue.FromScalar(5.0f));

        NumericAccumulator empty = new();
        first.Merge(empty);

        NumericResult result = (NumericResult)first.GetResult().Value!;
        Assert.Equal(1, result.Count);
        Assert.Equal(5.0, result.Mean);
    }

    [Fact]
    public void Merge_EmptyIntoEmpty_StaysEmpty()
    {
        NumericAccumulator first = new();
        NumericAccumulator second = new();

        first.Merge(second);

        NumericResult result = (NumericResult)first.GetResult().Value!;
        Assert.Equal(0, result.Count);
    }

    [Fact]
    public void StandardDeviation_CalculatedCorrectly()
    {
        NumericAccumulator accumulator = new();

        accumulator.Add(DataValue.FromScalar(2.0f));
        accumulator.Add(DataValue.FromScalar(4.0f));
        accumulator.Add(DataValue.FromScalar(4.0f));
        accumulator.Add(DataValue.FromScalar(4.0f));
        accumulator.Add(DataValue.FromScalar(5.0f));
        accumulator.Add(DataValue.FromScalar(5.0f));
        accumulator.Add(DataValue.FromScalar(7.0f));
        accumulator.Add(DataValue.FromScalar(9.0f));

        NumericResult result = (NumericResult)accumulator.GetResult().Value!;
        Assert.Equal(2.0, result.StandardDeviation, 1e-10);
    }

    [Fact]
    public void GetResult_HasCorrectName()
    {
        NumericAccumulator accumulator = new();
        Assert.Equal("numeric", accumulator.GetResult().Name);
    }

    [Fact]
    public void Add_WithZeros_CountsZerosCorrectly()
    {
        NumericAccumulator accumulator = new();

        accumulator.Add(DataValue.FromScalar(0.0f));
        accumulator.Add(DataValue.FromScalar(1.0f));
        accumulator.Add(DataValue.FromScalar(0.0f));
        accumulator.Add(DataValue.FromScalar(2.0f));

        NumericResult result = (NumericResult)accumulator.GetResult().Value!;
        Assert.Equal(2, result.ZeroCount);
        Assert.Equal(0.5, result.ZeroRatio, 1e-10);
    }

    [Fact]
    public void Add_AllZeros_ZeroRatioIsOne()
    {
        NumericAccumulator accumulator = new();

        accumulator.Add(DataValue.FromScalar(0.0f));
        accumulator.Add(DataValue.FromScalar(0.0f));
        accumulator.Add(DataValue.FromScalar(0.0f));

        NumericResult result = (NumericResult)accumulator.GetResult().Value!;
        Assert.Equal(3, result.ZeroCount);
        Assert.Equal(1.0, result.ZeroRatio, 1e-10);
    }

    [Fact]
    public void Add_NoZeros_ZeroCountIsZero()
    {
        NumericAccumulator accumulator = new();

        accumulator.Add(DataValue.FromScalar(1.0f));
        accumulator.Add(DataValue.FromScalar(2.0f));
        accumulator.Add(DataValue.FromScalar(3.0f));

        NumericResult result = (NumericResult)accumulator.GetResult().Value!;
        Assert.Equal(0, result.ZeroCount);
        Assert.Equal(0.0, result.ZeroRatio, 1e-10);
    }

    [Fact]
    public void Add_NoValues_ZeroRatioIsZero()
    {
        NumericAccumulator accumulator = new();

        NumericResult result = (NumericResult)accumulator.GetResult().Value!;
        Assert.Equal(0, result.ZeroCount);
        Assert.Equal(0.0, result.ZeroRatio, 1e-10);
    }

    [Fact]
    public void Merge_CombinesZeroCounts()
    {
        NumericAccumulator first = new();
        first.Add(DataValue.FromScalar(0.0f));
        first.Add(DataValue.FromScalar(1.0f));

        NumericAccumulator second = new();
        second.Add(DataValue.FromScalar(0.0f));
        second.Add(DataValue.FromScalar(0.0f));
        second.Add(DataValue.FromScalar(5.0f));

        first.Merge(second);

        NumericResult result = (NumericResult)first.GetResult().Value!;
        Assert.Equal(5, result.Count);
        Assert.Equal(3, result.ZeroCount);
        Assert.Equal(0.6, result.ZeroRatio, 1e-10);
    }

    [Fact]
    public void Add_WithClearOutlier_DetectsOutlier()
    {
        NumericAccumulator accumulator = new();

        // Add many similar values to establish a stable mean/stddev
        for (int i = 0; i < 100; i++)
        {
            accumulator.Add(DataValue.FromScalar(50.0f + (i % 5)));
        }

        // Add a value far from the mean
        accumulator.Add(DataValue.FromScalar(500.0f));

        NumericResult result = (NumericResult)accumulator.GetResult().Value!;
        Assert.True(result.OutlierCount > 0, "Expected at least one outlier detected");
        Assert.True(result.OutlierRatio > 0.0);
    }

    [Fact]
    public void Add_NoOutliers_OutlierCountIsZero()
    {
        NumericAccumulator accumulator = new();

        // Tight cluster of values — no outliers
        accumulator.Add(DataValue.FromScalar(10.0f));
        accumulator.Add(DataValue.FromScalar(10.1f));
        accumulator.Add(DataValue.FromScalar(10.2f));
        accumulator.Add(DataValue.FromScalar(9.9f));
        accumulator.Add(DataValue.FromScalar(9.8f));

        NumericResult result = (NumericResult)accumulator.GetResult().Value!;
        Assert.Equal(0, result.OutlierCount);
        Assert.Equal(0.0, result.OutlierRatio, 1e-10);
    }

    [Fact]
    public void Add_SingleValue_NoOutliersPossible()
    {
        NumericAccumulator accumulator = new();

        accumulator.Add(DataValue.FromScalar(42.0f));

        NumericResult result = (NumericResult)accumulator.GetResult().Value!;
        Assert.Equal(0, result.OutlierCount);
        Assert.Equal(0.0, result.OutlierRatio, 1e-10);
    }

    [Fact]
    public void Add_AllSameValue_StdDevZero_NoOutliers()
    {
        NumericAccumulator accumulator = new();

        accumulator.Add(DataValue.FromScalar(5.0f));
        accumulator.Add(DataValue.FromScalar(5.0f));
        accumulator.Add(DataValue.FromScalar(5.0f));
        accumulator.Add(DataValue.FromScalar(5.0f));

        NumericResult result = (NumericResult)accumulator.GetResult().Value!;
        Assert.Equal(0, result.OutlierCount);
        Assert.Equal(0.0, result.OutlierRatio, 1e-10);
    }

    [Fact]
    public void Add_NoValues_OutlierCountIsZero()
    {
        NumericAccumulator accumulator = new();

        NumericResult result = (NumericResult)accumulator.GetResult().Value!;
        Assert.Equal(0, result.OutlierCount);
        Assert.Equal(0.0, result.OutlierRatio, 1e-10);
    }

    [Fact]
    public void Merge_CombinesOutlierCounts()
    {
        NumericAccumulator first = new();
        for (int i = 0; i < 100; i++)
        {
            first.Add(DataValue.FromScalar(50.0f));
        }
        first.Add(DataValue.FromScalar(500.0f));

        NumericAccumulator second = new();
        for (int i = 0; i < 100; i++)
        {
            second.Add(DataValue.FromScalar(50.0f));
        }
        second.Add(DataValue.FromScalar(500.0f));

        long firstOutliers = ((NumericResult)first.GetResult().Value!).OutlierCount;
        long secondOutliers = ((NumericResult)second.GetResult().Value!).OutlierCount;

        first.Merge(second);

        NumericResult result = (NumericResult)first.GetResult().Value!;
        Assert.Equal(firstOutliers + secondOutliers, result.OutlierCount);
    }

    [Fact]
    public void Skewness_SymmetricDistribution_NearZero()
    {
        NumericAccumulator accumulator = new();

        // Symmetric distribution around 50: [1,2,...,99]
        for (int i = 1; i <= 99; i++)
        {
            accumulator.Add(DataValue.FromScalar(i));
        }

        NumericResult result = (NumericResult)accumulator.GetResult().Value!;
        Assert.Equal(0.0, result.Skewness, 1e-10);
    }

    [Fact]
    public void Skewness_RightSkewedDistribution_Positive()
    {
        NumericAccumulator accumulator = new();

        // Many small values and a few large ones → right-skewed
        for (int i = 0; i < 100; i++)
        {
            accumulator.Add(DataValue.FromScalar(1.0f));
        }

        for (int i = 0; i < 10; i++)
        {
            accumulator.Add(DataValue.FromScalar(100.0f));
        }

        NumericResult result = (NumericResult)accumulator.GetResult().Value!;
        Assert.True(result.Skewness > 0, $"Expected positive skewness for right-skewed data, got {result.Skewness}");
    }

    [Fact]
    public void Skewness_LeftSkewedDistribution_Negative()
    {
        NumericAccumulator accumulator = new();

        // Many large values and a few small ones → left-skewed
        for (int i = 0; i < 10; i++)
        {
            accumulator.Add(DataValue.FromScalar(1.0f));
        }

        for (int i = 0; i < 100; i++)
        {
            accumulator.Add(DataValue.FromScalar(100.0f));
        }

        NumericResult result = (NumericResult)accumulator.GetResult().Value!;
        Assert.True(result.Skewness < 0, $"Expected negative skewness for left-skewed data, got {result.Skewness}");
    }

    [Fact]
    public void Kurtosis_UniformDistribution_LessThanThree()
    {
        NumericAccumulator accumulator = new();

        // Uniform distribution has kurtosis = 1.8
        for (int i = 1; i <= 1000; i++)
        {
            accumulator.Add(DataValue.FromScalar(i));
        }

        NumericResult result = (NumericResult)accumulator.GetResult().Value!;
        Assert.True(result.Kurtosis < 3.0, $"Expected kurtosis < 3 for uniform data, got {result.Kurtosis}");
        Assert.True(result.Kurtosis > 1.0, $"Expected kurtosis > 1 for uniform data, got {result.Kurtosis}");
    }

    [Fact]
    public void Kurtosis_HeavyTails_GreaterThanThree()
    {
        NumericAccumulator accumulator = new();

        // Bulk of values in center, with extreme tails → leptokurtic (kurtosis > 3)
        for (int i = 0; i < 1000; i++)
        {
            accumulator.Add(DataValue.FromScalar(50.0f));
        }

        // Add extreme tail values
        for (int i = 0; i < 20; i++)
        {
            accumulator.Add(DataValue.FromScalar(-500.0f));
            accumulator.Add(DataValue.FromScalar(600.0f));
        }

        NumericResult result = (NumericResult)accumulator.GetResult().Value!;
        Assert.True(result.Kurtosis > 3.0, $"Expected kurtosis > 3 for heavy-tailed data, got {result.Kurtosis}");
    }

    [Fact]
    public void Skewness_SingleValue_ReturnsZero()
    {
        NumericAccumulator accumulator = new();

        accumulator.Add(DataValue.FromScalar(42.0f));

        NumericResult result = (NumericResult)accumulator.GetResult().Value!;
        Assert.Equal(0.0, result.Skewness);
    }

    [Fact]
    public void Kurtosis_TwoValues_ReturnsZero()
    {
        NumericAccumulator accumulator = new();

        accumulator.Add(DataValue.FromScalar(1.0f));
        accumulator.Add(DataValue.FromScalar(2.0f));

        NumericResult result = (NumericResult)accumulator.GetResult().Value!;
        Assert.Equal(0.0, result.Kurtosis);
    }

    [Fact]
    public void Skewness_AllIdenticalValues_ReturnsZero()
    {
        NumericAccumulator accumulator = new();

        for (int i = 0; i < 50; i++)
        {
            accumulator.Add(DataValue.FromScalar(7.0f));
        }

        NumericResult result = (NumericResult)accumulator.GetResult().Value!;
        Assert.Equal(0.0, result.Skewness);
        Assert.Equal(0.0, result.Kurtosis);
    }

    [Fact]
    public void Skewness_NoValues_ReturnsZero()
    {
        NumericAccumulator accumulator = new();

        NumericResult result = (NumericResult)accumulator.GetResult().Value!;
        Assert.Equal(0.0, result.Skewness);
        Assert.Equal(0.0, result.Kurtosis);
    }

    [Fact]
    public void Merge_PreservesSkewnessAndKurtosis()
    {
        // Single-pass accumulator with all values
        NumericAccumulator combined = new();
        for (int i = 0; i < 100; i++)
        {
            combined.Add(DataValue.FromScalar(1.0f));
        }

        for (int i = 0; i < 10; i++)
        {
            combined.Add(DataValue.FromScalar(100.0f));
        }

        // Split into two accumulators and merge
        NumericAccumulator first = new();
        for (int i = 0; i < 60; i++)
        {
            first.Add(DataValue.FromScalar(1.0f));
        }

        for (int i = 0; i < 5; i++)
        {
            first.Add(DataValue.FromScalar(100.0f));
        }

        NumericAccumulator second = new();
        for (int i = 0; i < 40; i++)
        {
            second.Add(DataValue.FromScalar(1.0f));
        }

        for (int i = 0; i < 5; i++)
        {
            second.Add(DataValue.FromScalar(100.0f));
        }

        first.Merge(second);

        NumericResult combinedResult = (NumericResult)combined.GetResult().Value!;
        NumericResult mergedResult = (NumericResult)first.GetResult().Value!;

        Assert.Equal(combinedResult.Skewness, mergedResult.Skewness, 1e-10);
        Assert.Equal(combinedResult.Kurtosis, mergedResult.Kurtosis, 1e-10);
    }

    [Fact]
    public void Skewness_KnownValues_MatchesExpected()
    {
        NumericAccumulator accumulator = new();

        // Values: [1, 2, 3, 4, 5]
        // Mean = 3, M2 = 10, M3 = 0 (symmetric)
        accumulator.Add(DataValue.FromScalar(1.0f));
        accumulator.Add(DataValue.FromScalar(2.0f));
        accumulator.Add(DataValue.FromScalar(3.0f));
        accumulator.Add(DataValue.FromScalar(4.0f));
        accumulator.Add(DataValue.FromScalar(5.0f));

        NumericResult result = (NumericResult)accumulator.GetResult().Value!;
        Assert.Equal(0.0, result.Skewness, 1e-10);
    }

    [Fact]
    public void Kurtosis_KnownValues_MatchesExpected()
    {
        NumericAccumulator accumulator = new();

        // Uniform discrete [1..5]: population kurtosis = n * M4 / M2^2
        // M2 = 10, M4 = 34, n = 5 → kurtosis = 5 * 34 / 100 = 1.7
        accumulator.Add(DataValue.FromScalar(1.0f));
        accumulator.Add(DataValue.FromScalar(2.0f));
        accumulator.Add(DataValue.FromScalar(3.0f));
        accumulator.Add(DataValue.FromScalar(4.0f));
        accumulator.Add(DataValue.FromScalar(5.0f));

        NumericResult result = (NumericResult)accumulator.GetResult().Value!;
        Assert.Equal(1.7, result.Kurtosis, 1e-10);
    }
}
