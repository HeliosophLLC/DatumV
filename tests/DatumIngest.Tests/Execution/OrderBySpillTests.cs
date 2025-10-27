using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for <see cref="OrderByOperator"/> spill-to-disk external sort
/// when <see cref="ExecutionContext.MemoryBudgetBytes"/> is set.
/// </summary>
public sealed class OrderBySpillTests : ServiceTestBase
{
    /// <summary>Tiny memory budget that forces spilling for even a few rows.</summary>
    private const long TinyBudget = 256;

    /// <summary>
    /// Ascending sort with spill produces the same result as a fully in-memory sort.
    /// </summary>
    [Fact]
    public async Task OrderBy_WithSpill_Ascending_ProducesCorrectOrder()
    {
        Row[] sourceRows =
        [
            MakeRow(("x", DataValue.FromFloat32(5f))),
            MakeRow(("x", DataValue.FromFloat32(1f))),
            MakeRow(("x", DataValue.FromFloat32(3f))),
            MakeRow(("x", DataValue.FromFloat32(2f))),
            MakeRow(("x", DataValue.FromFloat32(4f))),
            MakeRow(("x", DataValue.FromFloat32(8f))),
            MakeRow(("x", DataValue.FromFloat32(6f))),
            MakeRow(("x", DataValue.FromFloat32(7f))),
        ];

        MockOperator source = new(sourceRows);
        OrderByOperator orderBy = new(
            source,
            [new OrderByItem(new ColumnReference("x"), SortDirection.Ascending)]);

        List<Row> result = await CollectAsync(orderBy, CreateContext(TinyBudget));

        Assert.Equal(8, result.Count);

        for (int index = 0; index < result.Count; index++)
        {
            Assert.Equal((float)(index + 1), result[index]["x"].AsFloat32());
        }
    }

    /// <summary>
    /// Descending sort with spill produces correct descending order.
    /// </summary>
    [Fact]
    public async Task OrderBy_WithSpill_Descending_ProducesCorrectOrder()
    {
        Row[] sourceRows =
        [
            MakeRow(("x", DataValue.FromFloat32(3f))),
            MakeRow(("x", DataValue.FromFloat32(1f))),
            MakeRow(("x", DataValue.FromFloat32(4f))),
            MakeRow(("x", DataValue.FromFloat32(2f))),
        ];

        MockOperator source = new(sourceRows);
        OrderByOperator orderBy = new(
            source,
            [new OrderByItem(new ColumnReference("x"), SortDirection.Descending)]);

        List<Row> result = await CollectAsync(orderBy, CreateContext(TinyBudget));

        Assert.Equal(4, result.Count);
        Assert.Equal(4f, result[0]["x"].AsFloat32());
        Assert.Equal(3f, result[1]["x"].AsFloat32());
        Assert.Equal(2f, result[2]["x"].AsFloat32());
        Assert.Equal(1f, result[3]["x"].AsFloat32());
    }

    /// <summary>
    /// Sort with multiple columns and spill produces correct composite ordering.
    /// </summary>
    [Fact]
    public async Task OrderBy_WithSpill_MultipleColumns_ProducesCorrectOrder()
    {
        Row[] sourceRows =
        [
            MakeRow(("a", DataValue.FromFloat32(2f)), ("b", DataValue.FromFloat32(2f))),
            MakeRow(("a", DataValue.FromFloat32(1f)), ("b", DataValue.FromFloat32(3f))),
            MakeRow(("a", DataValue.FromFloat32(1f)), ("b", DataValue.FromFloat32(1f))),
            MakeRow(("a", DataValue.FromFloat32(2f)), ("b", DataValue.FromFloat32(1f))),
            MakeRow(("a", DataValue.FromFloat32(1f)), ("b", DataValue.FromFloat32(2f))),
            MakeRow(("a", DataValue.FromFloat32(2f)), ("b", DataValue.FromFloat32(3f))),
        ];

        MockOperator source = new(sourceRows);
        OrderByOperator orderBy = new(
            source,
            [
                new OrderByItem(new ColumnReference("a"), SortDirection.Ascending),
                new OrderByItem(new ColumnReference("b"), SortDirection.Ascending),
            ]);

        List<Row> result = await CollectAsync(orderBy, CreateContext(TinyBudget));

        Assert.Equal(6, result.Count);
        Assert.Equal(1f, result[0]["a"].AsFloat32());
        Assert.Equal(1f, result[0]["b"].AsFloat32());
        Assert.Equal(1f, result[1]["a"].AsFloat32());
        Assert.Equal(2f, result[1]["b"].AsFloat32());
        Assert.Equal(1f, result[2]["a"].AsFloat32());
        Assert.Equal(3f, result[2]["b"].AsFloat32());
        Assert.Equal(2f, result[3]["a"].AsFloat32());
        Assert.Equal(1f, result[3]["b"].AsFloat32());
    }

    /// <summary>
    /// Spill sort with string columns preserves correct ordinal ordering.
    /// </summary>
    [Fact]
    public async Task OrderBy_WithSpill_StringColumn_ProducesCorrectOrder()
    {
        Row[] sourceRows =
        [
            MakeRow(("name", DataValue.FromString("delta"))),
            MakeRow(("name", DataValue.FromString("alpha"))),
            MakeRow(("name", DataValue.FromString("charlie"))),
            MakeRow(("name", DataValue.FromString("bravo"))),
        ];

        MockOperator source = new(sourceRows);
        OrderByOperator orderBy = new(
            source,
            [new OrderByItem(new ColumnReference("name"), SortDirection.Ascending)]);

        List<Row> result = await CollectAsync(orderBy, CreateContext(TinyBudget));

        Assert.Equal(4, result.Count);
        Assert.Equal("alpha", result[0]["name"].AsString());
        Assert.Equal("bravo", result[1]["name"].AsString());
        Assert.Equal("charlie", result[2]["name"].AsString());
        Assert.Equal("delta", result[3]["name"].AsString());
    }

    /// <summary>
    /// Null values sort last in ascending order even when spilling to disk.
    /// </summary>
    [Fact]
    public async Task OrderBy_WithSpill_NullsSortLast()
    {
        Row[] sourceRows =
        [
            MakeRow(("x", DataValue.Null(DataKind.Float32))),
            MakeRow(("x", DataValue.FromFloat32(2f))),
            MakeRow(("x", DataValue.FromFloat32(1f))),
            MakeRow(("x", DataValue.Null(DataKind.Float32))),
            MakeRow(("x", DataValue.FromFloat32(3f))),
        ];

        MockOperator source = new(sourceRows);
        OrderByOperator orderBy = new(
            source,
            [new OrderByItem(new ColumnReference("x"), SortDirection.Ascending)]);

        List<Row> result = await CollectAsync(orderBy, CreateContext(TinyBudget));

        Assert.Equal(5, result.Count);
        Assert.Equal(1f, result[0]["x"].AsFloat32());
        Assert.Equal(2f, result[1]["x"].AsFloat32());
        Assert.Equal(3f, result[2]["x"].AsFloat32());
        Assert.True(result[3]["x"].IsNull);
        Assert.True(result[4]["x"].IsNull);
    }

    /// <summary>
    /// When no memory budget is set, the in-memory sort path is used and
    /// produces correct results (regression guard for the refactored code path).
    /// </summary>
    [Fact]
    public async Task OrderBy_NoBudget_InMemorySort_ProducesCorrectOrder()
    {
        Row[] sourceRows =
        [
            MakeRow(("x", DataValue.FromFloat32(3f))),
            MakeRow(("x", DataValue.FromFloat32(1f))),
            MakeRow(("x", DataValue.FromFloat32(2f))),
        ];

        MockOperator source = new(sourceRows);
        OrderByOperator orderBy = new(
            source,
            [new OrderByItem(new ColumnReference("x"), SortDirection.Ascending)]);

        List<Row> result = await CollectAsync(orderBy, CreateContext(memoryBudgetBytes: null));

        Assert.Equal(3, result.Count);
        Assert.Equal(1f, result[0]["x"].AsFloat32());
        Assert.Equal(2f, result[1]["x"].AsFloat32());
        Assert.Equal(3f, result[2]["x"].AsFloat32());
    }

    /// <summary>
    /// TopN (bounded heap) path is not affected by memory budget and still
    /// produces correct results.
    /// </summary>
    [Fact]
    public async Task OrderBy_TopN_WithBudget_ProducesCorrectOrder()
    {
        Row[] sourceRows =
        [
            MakeRow(("x", DataValue.FromFloat32(5f))),
            MakeRow(("x", DataValue.FromFloat32(1f))),
            MakeRow(("x", DataValue.FromFloat32(3f))),
            MakeRow(("x", DataValue.FromFloat32(2f))),
            MakeRow(("x", DataValue.FromFloat32(4f))),
        ];

        MockOperator source = new(sourceRows);
        OrderByOperator orderBy = new(
            source,
            [new OrderByItem(new ColumnReference("x"), SortDirection.Ascending)],
            topNRows: 3);

        List<Row> result = await CollectAsync(orderBy, CreateContext(TinyBudget));

        Assert.Equal(3, result.Count);
        Assert.Equal(1f, result[0]["x"].AsFloat32());
        Assert.Equal(2f, result[1]["x"].AsFloat32());
        Assert.Equal(3f, result[2]["x"].AsFloat32());
    }

    /// <summary>
    /// Empty source produces zero rows even with a memory budget.
    /// </summary>
    [Fact]
    public async Task OrderBy_WithSpill_EmptySource_ProducesNoRows()
    {
        MockOperator source = new();
        OrderByOperator orderBy = new(
            source,
            [new OrderByItem(new ColumnReference("x"), SortDirection.Ascending)]);

        List<Row> result = await CollectAsync(orderBy, CreateContext(TinyBudget));

        Assert.Empty(result);
    }

    /// <summary>
    /// Spill sort with a large number of rows produces the same result as in-memory sort.
    /// </summary>
    [Fact]
    public async Task OrderBy_WithSpill_ManyRows_MatchesInMemorySort()
    {
        const int rowCount = 500;
        Row[] sourceRows = new Row[rowCount];

        for (int index = 0; index < rowCount; index++)
        {
            sourceRows[index] = MakeRow(("x", DataValue.FromFloat32((float)(rowCount - index))));
        }

        MockOperator source1 = new(sourceRows);
        OrderByOperator spillSort = new(
            source1,
            [new OrderByItem(new ColumnReference("x"), SortDirection.Ascending)]);

        List<Row> spillResult = await CollectAsync(spillSort, CreateContext(TinyBudget));

        MockOperator source2 = new(sourceRows);
        OrderByOperator memorySort = new(
            source2,
            [new OrderByItem(new ColumnReference("x"), SortDirection.Ascending)]);

        List<Row> memoryResult = await CollectAsync(memorySort, CreateContext(memoryBudgetBytes: null));

        Assert.Equal(memoryResult.Count, spillResult.Count);

        for (int index = 0; index < memoryResult.Count; index++)
        {
            Assert.Equal(
                memoryResult[index]["x"].AsFloat32(),
                spillResult[index]["x"].AsFloat32());
        }
    }

    private static ExecutionContext CreateContext(long? memoryBudgetBytes = null)
    {
        return TestExecutionContext.Create(memoryBudgetBytes: memoryBudgetBytes);
    }

    private static async Task<List<Row>> CollectAsync(IQueryOperator op, ExecutionContext context)
    {
        return await op.CollectRowsAsync(context);
    }

    private static Row MakeRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = columns.Select(c => c.Name).ToArray();
        DataValue[] values = columns.Select(c => c.Value).ToArray();
        return new Row(names, values);
    }
}
