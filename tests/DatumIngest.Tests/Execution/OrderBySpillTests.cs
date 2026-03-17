using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for <see cref="OrderByOperator"/> spill-to-disk external sort
/// when <see cref="ExecutionContext.MemoryBudgetBytes"/> is set.
/// </summary>
public sealed class OrderBySpillTests : ServiceTestBase
{
    private static readonly string[] XColumns = ["x"];
    private static readonly string[] AbColumns = ["a", "b"];
    private static readonly string[] NameColumns = ["name"];

    /// <summary>Tiny memory budget that forces spilling for even a few rows.</summary>
    private const long TinyBudget = 256;

    /// <summary>
    /// Ascending sort with spill produces the same result as a fully in-memory sort.
    /// </summary>
    [Fact]
    public async Task OrderBy_WithSpill_Ascending_ProducesCorrectOrder()
    {
        MockOperator source = CreateMockOperator(XColumns,
            [5f],
            [1f],
            [3f],
            [2f],
            [4f],
            [8f],
            [6f],
            [7f]);
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
        MockOperator source = CreateMockOperator(XColumns,
            [3f],
            [1f],
            [4f],
            [2f]);
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
        MockOperator source = CreateMockOperator(AbColumns,
            [2f, 2f],
            [1f, 3f],
            [1f, 1f],
            [2f, 1f],
            [1f, 2f],
            [2f, 3f]);
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
        MockOperator source = CreateMockOperator(NameColumns,
            ["delta"],
            ["alpha"],
            ["charlie"],
            ["bravo"]);
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
        MockOperator source = CreateMockOperator(XColumns,
            [DataValue.Null(DataKind.Float32)],
            [2f],
            [1f],
            [DataValue.Null(DataKind.Float32)],
            [3f]);
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
        MockOperator source = CreateMockOperator(XColumns,
            [3f],
            [1f],
            [2f]);
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
        MockOperator source = CreateMockOperator(XColumns,
            [5f],
            [1f],
            [3f],
            [2f],
            [4f]);
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
        MockOperator source = CreateMockOperator(XColumns);
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
        object?[][] sourceRows = Enumerable.Range(0, rowCount)
            .Select(index => new object?[] { (float)(rowCount - index) })
            .ToArray();

        MockOperator source1 = CreateMockOperator(XColumns, rows: sourceRows);
        OrderByOperator spillSort = new(
            source1,
            [new OrderByItem(new ColumnReference("x"), SortDirection.Ascending)]);

        List<Row> spillResult = await CollectAsync(spillSort, CreateContext(TinyBudget));

        MockOperator source2 = CreateMockOperator(XColumns, rows: sourceRows);
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

        // Validate spill actually executed and the in-memory baseline didn't.
        Assert.True(spillSort.SpillingTriggered, "Tight budget should have triggered spill across 500 rows.");
        Assert.True(spillSort.SortedRunCount > 1, $"Expected multiple sorted runs under a 256-byte budget; got {spillSort.SortedRunCount}.");
        Assert.False(memorySort.SpillingTriggered);
        Assert.Equal(0, memorySort.SortedRunCount);
    }

    /// <summary>
    /// Tight budget with a small dataset must still flip <see cref="OrderByOperator.SpillingTriggered"/>
    /// — proves the existing 8-row spill tests above aren't silently bypassing
    /// the spill path.
    /// </summary>
    [Fact]
    public async Task OrderBy_WithSpill_AssertsSpillObserved()
    {
        MockOperator source = CreateMockOperator(XColumns,
            [5f], [1f], [3f], [2f], [4f], [8f], [6f], [7f]);
        OrderByOperator orderBy = new(
            source,
            [new OrderByItem(new ColumnReference("x"), SortDirection.Ascending)]);

        await CollectAsync(orderBy, CreateContext(TinyBudget));

        Assert.True(orderBy.SpillingTriggered);
        Assert.True(orderBy.SortedRunCount >= 1);
    }

    /// <summary>
    /// Generous budget should never trigger spill — the in-memory sort path is taken.
    /// </summary>
    [Fact]
    public async Task OrderBy_GenerousBudget_NoSpill()
    {
        const int rowCount = 200;
        object?[][] sourceRows = Enumerable.Range(0, rowCount)
            .Select(index => new object?[] { (float)(rowCount - index) })
            .ToArray();

        MockOperator source = CreateMockOperator(XColumns, rows: sourceRows);
        OrderByOperator orderBy = new(
            source,
            [new OrderByItem(new ColumnReference("x"), SortDirection.Ascending)]);

        await CollectAsync(orderBy, CreateContext(memoryBudgetBytes: 10 * 1024 * 1024));

        Assert.False(orderBy.SpillingTriggered);
        Assert.Equal(0, orderBy.SortedRunCount);
    }

    // ─────────────── Pool leak balance ───────────────

    [Fact]
    public async Task OrderBy_NoSpill_PoolDoesNotLeak()
    {
        Pool pool = GetService<Pool>();
        MockOperator source = CreateMockOperator(XColumns,
            [3f], [1f], [4f], [1f], [5f], [9f], [2f], [6f]);
        OrderByOperator orderBy = new(
            source,
            [new OrderByItem(new ColumnReference("x"), SortDirection.Ascending)]);

        await CollectAsync(orderBy, CreateContext(memoryBudgetBytes: null));
        orderBy.Dispose();

        AssertPoolBalanced(pool);
    }

    [Fact]
    public async Task OrderBy_WithSpill_PoolDoesNotLeak()
    {
        Pool pool = GetService<Pool>();
        const int rowCount = 500;
        object?[][] sourceRows = Enumerable.Range(0, rowCount)
            .Select(index => new object?[] { (float)(rowCount - index) })
            .ToArray();

        MockOperator source = CreateMockOperator(XColumns, rows: sourceRows);
        OrderByOperator orderBy = new(
            source,
            [new OrderByItem(new ColumnReference("x"), SortDirection.Ascending)]);

        await CollectAsync(orderBy, CreateContext(TinyBudget));
        Assert.True(orderBy.SpillingTriggered);

        orderBy.Dispose();
        AssertPoolBalanced(pool);
    }

    [Fact]
    public async Task OrderBy_TopN_PoolDoesNotLeak()
    {
        Pool pool = GetService<Pool>();
        MockOperator source = CreateMockOperator(XColumns,
            [5f], [1f], [3f], [2f], [4f], [8f], [6f], [7f]);
        OrderByOperator orderBy = new(
            source,
            [new OrderByItem(new ColumnReference("x"), SortDirection.Ascending)],
            topNRows: 3);

        List<Row> results = await CollectAsync(orderBy, CreateContext(memoryBudgetBytes: null));
        orderBy.Dispose();

        Assert.Equal(3, results.Count);
        AssertPoolBalanced(pool);
    }

    private static void AssertPoolBalanced(Pool pool)
    {
        long dvRent = pool.Backing.DataValueArrayRentCount;
        long dvReturn = pool.Backing.DataValueArrayReturnCount;
        long rbRent = pool.Backing.RowBatchRentCount;
        long rbReturn = pool.Backing.RowBatchReturnCount;
        long arenaRent = pool.Backing.ArenaRentCount;
        long arenaReleased = pool.Backing.ArenaFullyReleasedCount;

        Assert.True(
            dvRent == dvReturn && rbRent == rbReturn && arenaRent == arenaReleased,
            $"Pool not balanced — DataValue[] rent/return: {dvRent}/{dvReturn}, "
            + $"RowBatch rent/return: {rbRent}/{rbReturn}, "
            + $"Arena rent/fully-released: {arenaRent}/{arenaReleased}.");
    }

    private ExecutionContext CreateContext(long? memoryBudgetBytes = null)
    {
        return CreateExecutionContext(memoryBudgetBytes: memoryBudgetBytes);
    }

    private static async Task<List<Row>> CollectAsync(QueryOperator op, ExecutionContext context)
    {
        return await op.CollectRowsAsync(context);
    }
}
