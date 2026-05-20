using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Execution.Operators;
using Heliosoph.DatumV.Model;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Tests.Execution;

/// <summary>
/// Tests for <see cref="SkipOperator"/>.
/// </summary>
public sealed class SkipOperatorTests : ServiceTestBase
{
    private static readonly string[] IdColumns = ["id"];

    [Fact]
    public async Task Skip0_YieldsAllRows()
    {
        MockOperator source = CreateMockOperator(IdColumns,
            [1f],
            [2f],
            [3f]);

        SkipOperator skip = new(source, 0);
        List<Row> result = await CollectAsync(skip);

        Assert.Equal(3, result.Count);
        Assert.Equal(1f, result[0]["id"].AsFloat32());
        Assert.Equal(2f, result[1]["id"].AsFloat32());
        Assert.Equal(3f, result[2]["id"].AsFloat32());
    }

    [Fact]
    public async Task SkipN_YieldsRemainingRows()
    {
        MockOperator source = CreateMockOperator(IdColumns,
            [1f],
            [2f],
            [3f],
            [4f],
            [5f]);

        SkipOperator skip = new(source, 3);
        List<Row> result = await CollectAsync(skip);

        Assert.Equal(2, result.Count);
        Assert.Equal(4f, result[0]["id"].AsFloat32());
        Assert.Equal(5f, result[1]["id"].AsFloat32());
    }

    [Fact]
    public async Task SkipMoreThanAvailable_YieldsNothing()
    {
        MockOperator source = CreateMockOperator(IdColumns,
            [1f],
            [2f]);

        SkipOperator skip = new(source, 100);
        List<Row> result = await CollectAsync(skip);

        Assert.Empty(result);
    }

    [Fact]
    public async Task SkipExactCount_YieldsNothing()
    {
        MockOperator source = CreateMockOperator(IdColumns,
            [1f],
            [2f],
            [3f]);

        SkipOperator skip = new(source, 3);
        List<Row> result = await CollectAsync(skip);

        Assert.Empty(result);
    }

    private async Task<List<Row>> CollectAsync(QueryOperator op)
    {
        ExecutionContext context = CreateExecutionContext();
        return await op.CollectRowsAsync(context);
    }
}
