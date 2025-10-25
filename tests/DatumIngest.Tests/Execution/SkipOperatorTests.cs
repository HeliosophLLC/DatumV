using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for <see cref="SkipOperator"/>.
/// </summary>
public sealed class SkipOperatorTests
{
    [Fact]
    public async Task Skip0_YieldsAllRows()
    {
        MockOperator source = new(
            MakeRow(("id", DataValue.FromFloat32(1))),
            MakeRow(("id", DataValue.FromFloat32(2))),
            MakeRow(("id", DataValue.FromFloat32(3))));

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
        MockOperator source = new(
            MakeRow(("id", DataValue.FromFloat32(1))),
            MakeRow(("id", DataValue.FromFloat32(2))),
            MakeRow(("id", DataValue.FromFloat32(3))),
            MakeRow(("id", DataValue.FromFloat32(4))),
            MakeRow(("id", DataValue.FromFloat32(5))));

        SkipOperator skip = new(source, 3);
        List<Row> result = await CollectAsync(skip);

        Assert.Equal(2, result.Count);
        Assert.Equal(4f, result[0]["id"].AsFloat32());
        Assert.Equal(5f, result[1]["id"].AsFloat32());
    }

    [Fact]
    public async Task SkipMoreThanAvailable_YieldsNothing()
    {
        MockOperator source = new(
            MakeRow(("id", DataValue.FromFloat32(1))),
            MakeRow(("id", DataValue.FromFloat32(2))));

        SkipOperator skip = new(source, 100);
        List<Row> result = await CollectAsync(skip);

        Assert.Empty(result);
    }

    [Fact]
    public async Task SkipExactCount_YieldsNothing()
    {
        MockOperator source = new(
            MakeRow(("id", DataValue.FromFloat32(1))),
            MakeRow(("id", DataValue.FromFloat32(2))),
            MakeRow(("id", DataValue.FromFloat32(3))));

        SkipOperator skip = new(source, 3);
        List<Row> result = await CollectAsync(skip);

        Assert.Empty(result);
    }

    private static Row MakeRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = columns.Select(c => c.Name).ToArray();
        DataValue[] values = columns.Select(c => c.Value).ToArray();
        return new Row(names, values);
    }

    private static async Task<List<Row>> CollectAsync(IQueryOperator op)
    {
        ExecutionContext context = TestExecutionContext.Create();
        return await op.CollectRowsAsync(context);
    }
}
