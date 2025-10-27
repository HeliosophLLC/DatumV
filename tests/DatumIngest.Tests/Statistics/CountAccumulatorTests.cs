namespace DatumIngest.Tests.Statistics;

using System.Linq;
using DatumIngest.Model;
using DatumIngest.Statistics;
using DatumIngest.Statistics.Accumulators;

public sealed class CountAccumulatorTests : ServiceTestBase
{
    private readonly Arena _arena = new();

    public override void Dispose()
    {
        _arena.Dispose();
        base.Dispose();
    }

    [Fact]
    public void Add_NonNullValues_CountsCorrectly()
    {
        CountAccumulator accumulator = new();

        accumulator.Add(DataValue.FromFloat32(1.0f), _arena);
        accumulator.Add(DataValue.FromFloat32(2.0f), _arena);
        accumulator.Add(DataValue.FromString("hello", _arena), _arena);

        CountResult result = (CountResult)accumulator.GetResults().Single().Value!;
        Assert.Equal(3, result.NonNull);
        Assert.Equal(0, result.NullOrEmpty);
    }

    [Fact]
    public void Add_NullValues_CountsAsNullOrEmpty()
    {
        CountAccumulator accumulator = new();

        accumulator.Add(DataValue.Null(DataKind.Float32), _arena);
        accumulator.Add(DataValue.Null(DataKind.String), _arena);
        accumulator.Add(DataValue.FromFloat32(1.0f), _arena);

        CountResult result = (CountResult)accumulator.GetResults().Single().Value!;
        Assert.Equal(1, result.NonNull);
        Assert.Equal(2, result.NullOrEmpty);
    }

    [Fact]
    public void Add_EmptyStrings_CountsAsNullOrEmpty()
    {
        CountAccumulator accumulator = new();

        accumulator.Add(DataValue.FromString("", _arena), _arena);
        accumulator.Add(DataValue.FromString("hello", _arena), _arena);
        accumulator.Add(DataValue.FromString("", _arena), _arena);

        CountResult result = (CountResult)accumulator.GetResults().Single().Value!;
        Assert.Equal(1, result.NonNull);
        Assert.Equal(2, result.NullOrEmpty);
    }

    [Fact]
    public void Add_NoValues_ReturnsZeroCounts()
    {
        CountAccumulator accumulator = new();

        CountResult result = (CountResult)accumulator.GetResults().Single().Value!;
        Assert.Equal(0, result.NonNull);
        Assert.Equal(0, result.NullOrEmpty);
    }

    [Fact]
    public void GetResult_HasCorrectName()
    {
        CountAccumulator accumulator = new();
        Assert.Equal("count", accumulator.GetResults().Single().Name);
    }
}
