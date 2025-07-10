namespace Axon.QueryEngine.Tests.Statistics;

using Axon.QueryEngine.Model;
using Axon.QueryEngine.Statistics;
using Axon.QueryEngine.Statistics.Accumulators;

public sealed class StringLengthAccumulatorTests
{
    [Fact]
    public void Add_StringValues_TracksMinMaxLength()
    {
        StringLengthAccumulator accumulator = new();

        accumulator.Add(DataValue.FromString("hi"));
        accumulator.Add(DataValue.FromString("hello"));
        accumulator.Add(DataValue.FromString("greetings"));

        StringLengthResult result = (StringLengthResult)accumulator.GetResult().Value!;
        Assert.Equal(3, result.Count);
        Assert.Equal(2, result.MinLength);
        Assert.Equal(9, result.MaxLength);
    }

    [Fact]
    public void Add_SingleString_MinEqualsMax()
    {
        StringLengthAccumulator accumulator = new();

        accumulator.Add(DataValue.FromString("test"));

        StringLengthResult result = (StringLengthResult)accumulator.GetResult().Value!;
        Assert.Equal(1, result.Count);
        Assert.Equal(4, result.MinLength);
        Assert.Equal(4, result.MaxLength);
    }

    [Fact]
    public void Add_EmptyString_TracksZeroLength()
    {
        StringLengthAccumulator accumulator = new();

        accumulator.Add(DataValue.FromString(""));
        accumulator.Add(DataValue.FromString("abc"));

        StringLengthResult result = (StringLengthResult)accumulator.GetResult().Value!;
        Assert.Equal(2, result.Count);
        Assert.Equal(0, result.MinLength);
        Assert.Equal(3, result.MaxLength);
    }

    [Fact]
    public void Add_NullValues_AreSkipped()
    {
        StringLengthAccumulator accumulator = new();

        accumulator.Add(DataValue.Null(DataKind.String));
        accumulator.Add(DataValue.FromString("hello"));

        StringLengthResult result = (StringLengthResult)accumulator.GetResult().Value!;
        Assert.Equal(1, result.Count);
        Assert.Equal(5, result.MinLength);
    }

    [Fact]
    public void Add_NonStringValues_AreSkipped()
    {
        StringLengthAccumulator accumulator = new();

        accumulator.Add(DataValue.FromScalar(1.0f));
        accumulator.Add(DataValue.FromUInt8(42));

        StringLengthResult result = (StringLengthResult)accumulator.GetResult().Value!;
        Assert.Equal(0, result.Count);
        Assert.Equal(0, result.MinLength);
        Assert.Equal(0, result.MaxLength);
    }

    [Fact]
    public void Add_JsonValues_TracksLength()
    {
        StringLengthAccumulator accumulator = new();

        accumulator.Add(DataValue.FromJsonValue("{\"a\":1}"));
        accumulator.Add(DataValue.FromJsonValue("[1,2,3,4,5]"));

        StringLengthResult result = (StringLengthResult)accumulator.GetResult().Value!;
        Assert.Equal(2, result.Count);
        Assert.Equal(7, result.MinLength);
        Assert.Equal(11, result.MaxLength);
    }

    [Fact]
    public void Merge_CombinesResults()
    {
        StringLengthAccumulator first = new();
        first.Add(DataValue.FromString("ab"));
        first.Add(DataValue.FromString("abcd"));

        StringLengthAccumulator second = new();
        second.Add(DataValue.FromString("a"));
        second.Add(DataValue.FromString("abcdef"));

        first.Merge(second);

        StringLengthResult result = (StringLengthResult)first.GetResult().Value!;
        Assert.Equal(4, result.Count);
        Assert.Equal(1, result.MinLength);
        Assert.Equal(6, result.MaxLength);
    }

    [Fact]
    public void Add_NoValues_ReturnsZero()
    {
        StringLengthAccumulator accumulator = new();

        StringLengthResult result = (StringLengthResult)accumulator.GetResult().Value!;
        Assert.Equal(0, result.Count);
        Assert.Equal(0, result.MinLength);
        Assert.Equal(0, result.MaxLength);
    }

    [Fact]
    public void GetResult_HasCorrectName()
    {
        StringLengthAccumulator accumulator = new();
        Assert.Equal("string_length", accumulator.GetResult().Name);
    }
}
