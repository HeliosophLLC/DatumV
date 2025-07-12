namespace DatumIngest.Tests.Statistics;

using DatumIngest.Model;
using DatumIngest.Statistics;
using DatumIngest.Statistics.Accumulators;

public sealed class EntropyAccumulatorTests
{
    [Fact]
    public void GetResult_NoValues_ReturnsZero()
    {
        EntropyAccumulator accumulator = new();

        EntropyResult result = (EntropyResult)accumulator.GetResult().Value!;

        Assert.Equal(0.0, result.Value);
        Assert.False(result.Approximate);
    }

    [Fact]
    public void GetResult_SingleDistinctValue_ReturnsZero()
    {
        EntropyAccumulator accumulator = new();

        for (int i = 0; i < 100; i++)
        {
            accumulator.Add(DataValue.FromString("same"));
        }

        EntropyResult result = (EntropyResult)accumulator.GetResult().Value!;

        Assert.Equal(0.0, result.Value);
        Assert.False(result.Approximate);
    }

    [Fact]
    public void GetResult_TwoEqualFrequencyValues_ReturnsOne()
    {
        EntropyAccumulator accumulator = new();

        for (int i = 0; i < 500; i++)
        {
            accumulator.Add(DataValue.FromString("a"));
            accumulator.Add(DataValue.FromString("b"));
        }

        EntropyResult result = (EntropyResult)accumulator.GetResult().Value!;

        Assert.Equal(1.0, result.Value, precision: 10);
        Assert.False(result.Approximate);
    }

    [Fact]
    public void GetResult_UniformDistribution_ReturnsLog2N()
    {
        EntropyAccumulator accumulator = new();

        // 8 values, each appearing exactly once → H = log₂(8) = 3
        for (int i = 0; i < 8; i++)
        {
            accumulator.Add(DataValue.FromScalar(i));
        }

        EntropyResult result = (EntropyResult)accumulator.GetResult().Value!;

        Assert.Equal(3.0, result.Value, precision: 10);
    }

    [Fact]
    public void GetResult_SkewedDistribution_ReturnsLessThanMaxEntropy()
    {
        EntropyAccumulator accumulator = new();

        // "a" appears 90 times, "b" appears 10 times
        for (int i = 0; i < 90; i++)
        {
            accumulator.Add(DataValue.FromString("a"));
        }

        for (int i = 0; i < 10; i++)
        {
            accumulator.Add(DataValue.FromString("b"));
        }

        EntropyResult result = (EntropyResult)accumulator.GetResult().Value!;

        // Entropy should be between 0 and 1 (log₂(2))
        Assert.True(result.Value > 0);
        Assert.True(result.Value < 1.0);
    }

    [Fact]
    public void Add_NullValues_AreIgnored()
    {
        EntropyAccumulator accumulator = new();

        accumulator.Add(DataValue.Null(DataKind.String));
        accumulator.Add(DataValue.FromString("a"));
        accumulator.Add(DataValue.Null(DataKind.String));
        accumulator.Add(DataValue.FromString("b"));

        EntropyResult result = (EntropyResult)accumulator.GetResult().Value!;

        Assert.Equal(1.0, result.Value, precision: 10);
    }

    [Fact]
    public void GetResult_NumericValues_ComputesEntropy()
    {
        EntropyAccumulator accumulator = new();

        // UInt8 values: 4 distinct → H = log₂(4) = 2
        for (int i = 0; i < 4; i++)
        {
            accumulator.Add(DataValue.FromUInt8((byte)i));
        }

        EntropyResult result = (EntropyResult)accumulator.GetResult().Value!;

        Assert.Equal(2.0, result.Value, precision: 10);
    }

    [Fact]
    public void Merge_CombinesFrequencies()
    {
        EntropyAccumulator a = new();
        EntropyAccumulator b = new();

        for (int i = 0; i < 50; i++)
        {
            a.Add(DataValue.FromString("x"));
        }

        for (int i = 0; i < 50; i++)
        {
            b.Add(DataValue.FromString("y"));
        }

        a.Merge(b);

        EntropyResult result = (EntropyResult)a.GetResult().Value!;

        Assert.Equal(1.0, result.Value, precision: 10);
    }

    [Fact]
    public void GetResult_ResultNameIsEntropy()
    {
        EntropyAccumulator accumulator = new();
        accumulator.Add(DataValue.FromScalar(1.0f));

        StatisticResult result = accumulator.GetResult();

        Assert.Equal("entropy", result.Name);
    }
}
