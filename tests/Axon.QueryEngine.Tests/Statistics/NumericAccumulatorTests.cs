namespace Axon.QueryEngine.Tests.Statistics;

using Axon.QueryEngine.Model;
using Axon.QueryEngine.Statistics;
using Axon.QueryEngine.Statistics.Accumulators;

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
}
