namespace DatumIngest.Tests.Statistics;

using System.Linq;
using DatumIngest.Model;
using DatumIngest.Statistics.Accumulators;

public sealed class StringLengthAccumulatorTests : ServiceTestBase
{
    private readonly Arena _arena;

    public StringLengthAccumulatorTests()
    {
        _arena = CreateArena();
    }


    public override void Dispose()
    {
        _arena.Dispose();
        base.Dispose();
    }

    [Fact]
    public void Add_StringValues_TracksMinMaxLength()
    {
        StringLengthAccumulator accumulator = new();

        accumulator.Add(DataValue.FromString("hi", _arena), _arena);
        accumulator.Add(DataValue.FromString("hello", _arena), _arena);
        accumulator.Add(DataValue.FromString("greetings", _arena), _arena);

        StringLengthResult result = (StringLengthResult)accumulator.GetResults().Single().Value!;
        Assert.Equal(3, result.Count);
        Assert.Equal(2, result.MinLength);
        Assert.Equal(9, result.MaxLength);
    }

    [Fact]
    public void Add_SingleString_MinEqualsMax()
    {
        StringLengthAccumulator accumulator = new();

        accumulator.Add(DataValue.FromString("test", _arena), _arena);

        StringLengthResult result = (StringLengthResult)accumulator.GetResults().Single().Value!;
        Assert.Equal(1, result.Count);
        Assert.Equal(4, result.MinLength);
        Assert.Equal(4, result.MaxLength);
    }

    [Fact]
    public void Add_EmptyString_TracksZeroLength()
    {
        StringLengthAccumulator accumulator = new();

        accumulator.Add(DataValue.FromString("", _arena), _arena);
        accumulator.Add(DataValue.FromString("abc", _arena), _arena);

        StringLengthResult result = (StringLengthResult)accumulator.GetResults().Single().Value!;
        Assert.Equal(2, result.Count);
        Assert.Equal(0, result.MinLength);
        Assert.Equal(3, result.MaxLength);
    }

    [Fact]
    public void Add_NullValues_AreSkipped()
    {
        StringLengthAccumulator accumulator = new();

        accumulator.Add(DataValue.Null(DataKind.String), _arena);
        accumulator.Add(DataValue.FromString("hello", _arena), _arena);

        StringLengthResult result = (StringLengthResult)accumulator.GetResults().Single().Value!;
        Assert.Equal(1, result.Count);
        Assert.Equal(5, result.MinLength);
    }

    [Fact]
    public void Add_NonStringValues_AreSkipped()
    {
        StringLengthAccumulator accumulator = new();

        accumulator.Add(DataValue.FromFloat32(1.0f), _arena);
        accumulator.Add(DataValue.FromUInt8(42), _arena);

        StringLengthResult result = (StringLengthResult)accumulator.GetResults().Single().Value!;
        Assert.Equal(0, result.Count);
        Assert.Equal(0, result.MinLength);
        Assert.Equal(0, result.MaxLength);
    }

    [Fact]
    public void Add_NoValues_ReturnsZero()
    {
        StringLengthAccumulator accumulator = new();

        StringLengthResult result = (StringLengthResult)accumulator.GetResults().Single().Value!;
        Assert.Equal(0, result.Count);
        Assert.Equal(0, result.MinLength);
        Assert.Equal(0, result.MaxLength);
    }

    [Fact]
    public void GetResult_HasCorrectName()
    {
        StringLengthAccumulator accumulator = new();
        Assert.Equal("string_length", accumulator.GetResults().Single().Name);
    }
}
