namespace DatumQuery.Tests.Statistics;

using DatumQuery.Model;
using DatumQuery.Statistics;
using DatumQuery.Statistics.Accumulators;

public sealed class MissingRunsAccumulatorTests
{
    [Fact]
    public void Add_NoValues_ReturnsZeroRuns()
    {
        MissingRunsAccumulator accumulator = new();

        MissingRunsResult result = (MissingRunsResult)accumulator.GetResult().Value!;
        Assert.Equal(0, result.RunCount);
    }

    [Fact]
    public void Add_NoNulls_ReturnsZeroRuns()
    {
        MissingRunsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromScalar(1.0f));
        accumulator.Add(DataValue.FromScalar(2.0f));
        accumulator.Add(DataValue.FromScalar(3.0f));

        MissingRunsResult result = (MissingRunsResult)accumulator.GetResult().Value!;
        Assert.Equal(0, result.RunCount);
    }

    [Fact]
    public void Add_SingleNull_ReturnsOneRun()
    {
        MissingRunsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromScalar(1.0f));
        accumulator.Add(DataValue.Null(DataKind.Scalar));
        accumulator.Add(DataValue.FromScalar(3.0f));

        MissingRunsResult result = (MissingRunsResult)accumulator.GetResult().Value!;
        Assert.Equal(1, result.RunCount);
    }

    [Fact]
    public void Add_ConsecutiveNulls_CountsAsOneRun()
    {
        MissingRunsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromScalar(1.0f));
        accumulator.Add(DataValue.Null(DataKind.Scalar));
        accumulator.Add(DataValue.Null(DataKind.Scalar));
        accumulator.Add(DataValue.Null(DataKind.Scalar));
        accumulator.Add(DataValue.FromScalar(5.0f));

        MissingRunsResult result = (MissingRunsResult)accumulator.GetResult().Value!;
        Assert.Equal(1, result.RunCount);
    }

    [Fact]
    public void Add_SeparatedNullGroups_CountsEachRun()
    {
        MissingRunsAccumulator accumulator = new();

        accumulator.Add(DataValue.Null(DataKind.Scalar));
        accumulator.Add(DataValue.Null(DataKind.Scalar));
        accumulator.Add(DataValue.FromScalar(1.0f));
        accumulator.Add(DataValue.Null(DataKind.Scalar));

        MissingRunsResult result = (MissingRunsResult)accumulator.GetResult().Value!;
        Assert.Equal(2, result.RunCount);
    }

    [Fact]
    public void Add_AllNulls_ReturnsOneRun()
    {
        MissingRunsAccumulator accumulator = new();

        accumulator.Add(DataValue.Null(DataKind.Scalar));
        accumulator.Add(DataValue.Null(DataKind.Scalar));
        accumulator.Add(DataValue.Null(DataKind.Scalar));

        MissingRunsResult result = (MissingRunsResult)accumulator.GetResult().Value!;
        Assert.Equal(1, result.RunCount);
    }

    [Fact]
    public void Add_EmptyStrings_TreatedAsMissing()
    {
        MissingRunsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromString("hello"));
        accumulator.Add(DataValue.FromString(""));
        accumulator.Add(DataValue.FromString(""));
        accumulator.Add(DataValue.FromString("world"));
        accumulator.Add(DataValue.FromString(""));

        MissingRunsResult result = (MissingRunsResult)accumulator.GetResult().Value!;
        Assert.Equal(2, result.RunCount);
    }

    [Fact]
    public void Add_ThreeRuns_CountsCorrectly()
    {
        MissingRunsAccumulator accumulator = new();

        // Run 1: leading null
        accumulator.Add(DataValue.Null(DataKind.Scalar));
        accumulator.Add(DataValue.FromScalar(1.0f));
        // Run 2: middle null
        accumulator.Add(DataValue.Null(DataKind.Scalar));
        accumulator.Add(DataValue.FromScalar(2.0f));
        // Run 3: trailing null
        accumulator.Add(DataValue.Null(DataKind.Scalar));

        MissingRunsResult result = (MissingRunsResult)accumulator.GetResult().Value!;
        Assert.Equal(3, result.RunCount);
    }

    [Fact]
    public void Merge_BoundaryCoalesces_WhenChunksShareNullBorder()
    {
        // Chunk 1: [1, null, null]  → 1 run, ends with null
        MissingRunsAccumulator first = new();
        first.Add(DataValue.FromScalar(1.0f));
        first.Add(DataValue.Null(DataKind.Scalar));
        first.Add(DataValue.Null(DataKind.Scalar));

        // Chunk 2: [null, 2]  → 1 run, starts with null
        MissingRunsAccumulator second = new();
        second.Add(DataValue.Null(DataKind.Scalar));
        second.Add(DataValue.FromScalar(2.0f));

        first.Merge(second);

        // The two runs merge into one continuous run
        MissingRunsResult result = (MissingRunsResult)first.GetResult().Value!;
        Assert.Equal(1, result.RunCount);
    }

    [Fact]
    public void Merge_NoBoundaryCoalesce_WhenChunksDontBorderOnNulls()
    {
        // Chunk 1: [null, 1]  → 1 run, ends with non-null
        MissingRunsAccumulator first = new();
        first.Add(DataValue.Null(DataKind.Scalar));
        first.Add(DataValue.FromScalar(1.0f));

        // Chunk 2: [2, null]  → 1 run, starts with non-null
        MissingRunsAccumulator second = new();
        second.Add(DataValue.FromScalar(2.0f));
        second.Add(DataValue.Null(DataKind.Scalar));

        first.Merge(second);

        // No coalescing: 1 + 1 = 2 runs
        MissingRunsResult result = (MissingRunsResult)first.GetResult().Value!;
        Assert.Equal(2, result.RunCount);
    }

    [Fact]
    public void Merge_EmptySecond_PreservesFirst()
    {
        MissingRunsAccumulator first = new();
        first.Add(DataValue.Null(DataKind.Scalar));
        first.Add(DataValue.FromScalar(1.0f));
        first.Add(DataValue.Null(DataKind.Scalar));

        MissingRunsAccumulator second = new();

        first.Merge(second);

        MissingRunsResult result = (MissingRunsResult)first.GetResult().Value!;
        Assert.Equal(2, result.RunCount);
    }

    [Fact]
    public void Merge_EmptyFirst_TakesSecond()
    {
        MissingRunsAccumulator first = new();

        MissingRunsAccumulator second = new();
        second.Add(DataValue.Null(DataKind.Scalar));
        second.Add(DataValue.FromScalar(1.0f));
        second.Add(DataValue.Null(DataKind.Scalar));

        first.Merge(second);

        MissingRunsResult result = (MissingRunsResult)first.GetResult().Value!;
        Assert.Equal(2, result.RunCount);
    }

    [Fact]
    public void Merge_AllNullBothChunks_CoalescesToOneRun()
    {
        MissingRunsAccumulator first = new();
        first.Add(DataValue.Null(DataKind.Scalar));
        first.Add(DataValue.Null(DataKind.Scalar));

        MissingRunsAccumulator second = new();
        second.Add(DataValue.Null(DataKind.Scalar));
        second.Add(DataValue.Null(DataKind.Scalar));

        first.Merge(second);

        MissingRunsResult result = (MissingRunsResult)first.GetResult().Value!;
        Assert.Equal(1, result.RunCount);
    }

    [Fact]
    public void GetResult_HasCorrectName()
    {
        MissingRunsAccumulator accumulator = new();
        Assert.Equal("missing_runs", accumulator.GetResult().Name);
    }
}
