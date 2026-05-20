namespace Heliosoph.DatumV.Tests.Statistics;

using System.Linq;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Statistics.Accumulators;

public sealed class MissingRunsAccumulatorTests : ServiceTestBase
{
    private readonly Arena _arena;

    public MissingRunsAccumulatorTests()
    {
        _arena = CreateArena();
    }

    public override void Dispose()
    {
        _arena.Dispose();
        base.Dispose();
    }

    [Fact]
    public void Add_NoValues_ReturnsZeroRuns()
    {
        MissingRunsAccumulator accumulator = new();

        MissingRunsResult result = (MissingRunsResult)accumulator.GetResults().Single().Value!;
        Assert.Equal(0, result.RunCount);
    }

    [Fact]
    public void Add_NoNulls_ReturnsZeroRuns()
    {
        MissingRunsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromFloat32(1.0f), _arena);
        accumulator.Add(DataValue.FromFloat32(2.0f), _arena);
        accumulator.Add(DataValue.FromFloat32(3.0f), _arena);

        MissingRunsResult result = (MissingRunsResult)accumulator.GetResults().Single().Value!;
        Assert.Equal(0, result.RunCount);
    }

    [Fact]
    public void Add_SingleNull_ReturnsOneRun()
    {
        MissingRunsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromFloat32(1.0f), _arena);
        accumulator.Add(DataValue.Null(DataKind.Float32), _arena);
        accumulator.Add(DataValue.FromFloat32(3.0f), _arena);

        MissingRunsResult result = (MissingRunsResult)accumulator.GetResults().Single().Value!;
        Assert.Equal(1, result.RunCount);
    }

    [Fact]
    public void Add_ConsecutiveNulls_CountsAsOneRun()
    {
        MissingRunsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromFloat32(1.0f), _arena);
        accumulator.Add(DataValue.Null(DataKind.Float32), _arena);
        accumulator.Add(DataValue.Null(DataKind.Float32), _arena);
        accumulator.Add(DataValue.Null(DataKind.Float32), _arena);
        accumulator.Add(DataValue.FromFloat32(5.0f), _arena);

        MissingRunsResult result = (MissingRunsResult)accumulator.GetResults().Single().Value!;
        Assert.Equal(1, result.RunCount);
    }

    [Fact]
    public void Add_SeparatedNullGroups_CountsEachRun()
    {
        MissingRunsAccumulator accumulator = new();

        accumulator.Add(DataValue.Null(DataKind.Float32), _arena);
        accumulator.Add(DataValue.Null(DataKind.Float32), _arena);
        accumulator.Add(DataValue.FromFloat32(1.0f), _arena);
        accumulator.Add(DataValue.Null(DataKind.Float32), _arena);

        MissingRunsResult result = (MissingRunsResult)accumulator.GetResults().Single().Value!;
        Assert.Equal(2, result.RunCount);
    }

    [Fact]
    public void Add_AllNulls_ReturnsOneRun()
    {
        MissingRunsAccumulator accumulator = new();

        accumulator.Add(DataValue.Null(DataKind.Float32), _arena);
        accumulator.Add(DataValue.Null(DataKind.Float32), _arena);
        accumulator.Add(DataValue.Null(DataKind.Float32), _arena);

        MissingRunsResult result = (MissingRunsResult)accumulator.GetResults().Single().Value!;
        Assert.Equal(1, result.RunCount);
    }

    [Fact]
    public void Add_EmptyStrings_TreatedAsMissing()
    {
        MissingRunsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromString("hello", _arena), _arena);
        accumulator.Add(DataValue.FromString("", _arena), _arena);
        accumulator.Add(DataValue.FromString("", _arena), _arena);
        accumulator.Add(DataValue.FromString("world", _arena), _arena);
        accumulator.Add(DataValue.FromString("", _arena), _arena);

        MissingRunsResult result = (MissingRunsResult)accumulator.GetResults().Single().Value!;
        Assert.Equal(2, result.RunCount);
    }

    [Fact]
    public void Add_ThreeRuns_CountsCorrectly()
    {
        MissingRunsAccumulator accumulator = new();

        // Run 1: leading null
        accumulator.Add(DataValue.Null(DataKind.Float32), _arena);
        accumulator.Add(DataValue.FromFloat32(1.0f), _arena);
        // Run 2: middle null
        accumulator.Add(DataValue.Null(DataKind.Float32), _arena);
        accumulator.Add(DataValue.FromFloat32(2.0f), _arena);
        // Run 3: trailing null
        accumulator.Add(DataValue.Null(DataKind.Float32), _arena);

        MissingRunsResult result = (MissingRunsResult)accumulator.GetResults().Single().Value!;
        Assert.Equal(3, result.RunCount);
    }

    [Fact]
    public void GetResult_HasCorrectName()
    {
        MissingRunsAccumulator accumulator = new();
        Assert.Equal("missing_runs", accumulator.GetResults().Single().Name);
    }
}
