namespace Axon.QueryEngine.Tests.Statistics;

using Axon.QueryEngine.Model;
using Axon.QueryEngine.Statistics;
using Axon.QueryEngine.Statistics.Accumulators;

public sealed class CountAccumulatorTests
{
    [Fact]
    public void Add_NonNullValues_CountsCorrectly()
    {
        CountAccumulator accumulator = new();

        accumulator.Add(DataValue.FromScalar(1.0f));
        accumulator.Add(DataValue.FromScalar(2.0f));
        accumulator.Add(DataValue.FromString("hello"));

        CountResult result = (CountResult)accumulator.GetResult().Value!;
        Assert.Equal(3, result.NonNull);
        Assert.Equal(0, result.NullOrEmpty);
    }

    [Fact]
    public void Add_NullValues_CountsAsNullOrEmpty()
    {
        CountAccumulator accumulator = new();

        accumulator.Add(DataValue.Null(DataKind.Scalar));
        accumulator.Add(DataValue.Null(DataKind.String));
        accumulator.Add(DataValue.FromScalar(1.0f));

        CountResult result = (CountResult)accumulator.GetResult().Value!;
        Assert.Equal(1, result.NonNull);
        Assert.Equal(2, result.NullOrEmpty);
    }

    [Fact]
    public void Add_EmptyStrings_CountsAsNullOrEmpty()
    {
        CountAccumulator accumulator = new();

        accumulator.Add(DataValue.FromString(""));
        accumulator.Add(DataValue.FromString("hello"));
        accumulator.Add(DataValue.FromString(""));

        CountResult result = (CountResult)accumulator.GetResult().Value!;
        Assert.Equal(1, result.NonNull);
        Assert.Equal(2, result.NullOrEmpty);
    }

    [Fact]
    public void Add_NoValues_ReturnsZeroCounts()
    {
        CountAccumulator accumulator = new();

        CountResult result = (CountResult)accumulator.GetResult().Value!;
        Assert.Equal(0, result.NonNull);
        Assert.Equal(0, result.NullOrEmpty);
    }

    [Fact]
    public void Merge_CombinesCounts()
    {
        CountAccumulator first = new();
        first.Add(DataValue.FromScalar(1.0f));
        first.Add(DataValue.Null(DataKind.Scalar));

        CountAccumulator second = new();
        second.Add(DataValue.FromScalar(2.0f));
        second.Add(DataValue.FromScalar(3.0f));
        second.Add(DataValue.Null(DataKind.Scalar));

        first.Merge(second);

        CountResult result = (CountResult)first.GetResult().Value!;
        Assert.Equal(3, result.NonNull);
        Assert.Equal(2, result.NullOrEmpty);
    }

    [Fact]
    public void GetResult_HasCorrectName()
    {
        CountAccumulator accumulator = new();
        Assert.Equal("count", accumulator.GetResult().Name);
    }
}
