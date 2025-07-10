namespace Axon.QueryEngine.Tests.Statistics;

using Axon.QueryEngine.Model;
using Axon.QueryEngine.Statistics;
using Axon.QueryEngine.Statistics.Accumulators;

public sealed class HistogramAccumulatorTests
{
    [Fact]
    public void Add_SingleValue_SingleBin()
    {
        HistogramAccumulator accumulator = new();

        accumulator.Add(DataValue.FromScalar(5.0f));

        HistogramResult result = (HistogramResult)accumulator.GetResult().Value!;
        Assert.Single(result.Counts);
        Assert.Equal(1, result.Counts[0]);
    }

    [Fact]
    public void Add_UniformValues_DistributesAcrossBins()
    {
        HistogramAccumulator accumulator = new(binCount: 10);

        for (int i = 0; i <= 100; i++)
        {
            accumulator.Add(DataValue.FromScalar(i));
        }

        HistogramResult result = (HistogramResult)accumulator.GetResult().Value!;
        Assert.Equal(10, result.Counts.Count);
        Assert.Equal(11, result.BinEdges.Count);
        Assert.Equal(0.0, result.BinEdges[0]);
        Assert.Equal(100.0, result.BinEdges[^1]);

        long totalInBins = result.Counts.Sum();
        Assert.Equal(101, totalInBins);
    }

    [Fact]
    public void Add_UInt8Values_TracksCorrectly()
    {
        HistogramAccumulator accumulator = new(binCount: 5);

        accumulator.Add(DataValue.FromUInt8(0));
        accumulator.Add(DataValue.FromUInt8(50));
        accumulator.Add(DataValue.FromUInt8(100));
        accumulator.Add(DataValue.FromUInt8(200));
        accumulator.Add(DataValue.FromUInt8(255));

        HistogramResult result = (HistogramResult)accumulator.GetResult().Value!;
        Assert.Equal(5, result.Counts.Count);
        Assert.Equal(0.0, result.BinEdges[0]);
        Assert.Equal(255.0, result.BinEdges[^1]);

        long totalInBins = result.Counts.Sum();
        Assert.Equal(5, totalInBins);
    }

    [Fact]
    public void Add_IdenticalValues_HandledGracefully()
    {
        HistogramAccumulator accumulator = new(binCount: 10);

        for (int i = 0; i < 100; i++)
        {
            accumulator.Add(DataValue.FromScalar(42.0f));
        }

        HistogramResult result = (HistogramResult)accumulator.GetResult().Value!;
        Assert.Single(result.Counts);
        Assert.Equal(100, result.Counts[0]);
    }

    [Fact]
    public void Add_NullValues_Ignored()
    {
        HistogramAccumulator accumulator = new();

        accumulator.Add(DataValue.Null(DataKind.Scalar));
        accumulator.Add(DataValue.FromScalar(1.0f));

        HistogramResult result = (HistogramResult)accumulator.GetResult().Value!;
        Assert.Equal(1, accumulator.TotalCount);
    }

    [Fact]
    public void Add_NoValues_EmptyHistogram()
    {
        HistogramAccumulator accumulator = new();

        HistogramResult result = (HistogramResult)accumulator.GetResult().Value!;
        Assert.Empty(result.BinEdges);
        Assert.Empty(result.Counts);
    }

    [Fact]
    public void GetResult_HasCorrectName()
    {
        HistogramAccumulator accumulator = new();
        Assert.Equal("histogram", accumulator.GetResult().Name);
    }

    [Fact]
    public void Merge_TwoAccumulators_CombinesSamples()
    {
        HistogramAccumulator first = new(binCount: 5);
        HistogramAccumulator second = new(binCount: 5);

        for (int i = 0; i < 50; i++)
        {
            first.Add(DataValue.FromScalar(i));
        }

        for (int i = 50; i < 100; i++)
        {
            second.Add(DataValue.FromScalar(i));
        }

        first.Merge(second);
        HistogramResult result = (HistogramResult)first.GetResult().Value!;

        Assert.Equal(100, first.TotalCount);
        long totalInBins = result.Counts.Sum();
        Assert.Equal(100, totalInBins);
    }
}
