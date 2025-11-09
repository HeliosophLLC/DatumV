using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for the <see cref="SetOperationOperator"/> covering all six
/// set operation strategies (UNION/INTERSECT/EXCEPT × ALL/DISTINCT)
/// plus parser integration and edge cases.
/// </summary>
public sealed class SetOperationTests : ServiceTestBase
{
    private static readonly string[] XColumns = ["x"];

    private async Task<List<Row>> CollectAsync(
        IQueryOperator op, ExecutionContext? context = null)
    {
        context ??= CreateExecutionContext();
        return await op.CollectRowsAsync(context);
    }

    // ─────────────── Parsing ───────────────

    [Fact]
    public void Parse_UnionAll()
    {
        QueryExpression result = SqlParser.Parse("SELECT a FROM t1 UNION ALL SELECT a FROM t2");

        CompoundQueryExpression compound = Assert.IsType<CompoundQueryExpression>(result);
        Assert.Equal(SetOperationType.Union, compound.OperationType);
        Assert.True(compound.All);
    }

    [Fact]
    public void Parse_UnionDistinct()
    {
        QueryExpression result = SqlParser.Parse("SELECT a FROM t1 UNION SELECT a FROM t2");

        CompoundQueryExpression compound = Assert.IsType<CompoundQueryExpression>(result);
        Assert.Equal(SetOperationType.Union, compound.OperationType);
        Assert.False(compound.All);
    }

    [Fact]
    public void Parse_IntersectAll()
    {
        QueryExpression result = SqlParser.Parse("SELECT a FROM t1 INTERSECT ALL SELECT a FROM t2");

        CompoundQueryExpression compound = Assert.IsType<CompoundQueryExpression>(result);
        Assert.Equal(SetOperationType.Intersect, compound.OperationType);
        Assert.True(compound.All);
    }

    [Fact]
    public void Parse_IntersectDistinct()
    {
        QueryExpression result = SqlParser.Parse("SELECT a FROM t1 INTERSECT SELECT a FROM t2");

        CompoundQueryExpression compound = Assert.IsType<CompoundQueryExpression>(result);
        Assert.Equal(SetOperationType.Intersect, compound.OperationType);
        Assert.False(compound.All);
    }

    [Fact]
    public void Parse_ExceptAll()
    {
        QueryExpression result = SqlParser.Parse("SELECT a FROM t1 EXCEPT ALL SELECT a FROM t2");

        CompoundQueryExpression compound = Assert.IsType<CompoundQueryExpression>(result);
        Assert.Equal(SetOperationType.Except, compound.OperationType);
        Assert.True(compound.All);
    }

    [Fact]
    public void Parse_ExceptDistinct()
    {
        QueryExpression result = SqlParser.Parse("SELECT a FROM t1 EXCEPT SELECT a FROM t2");

        CompoundQueryExpression compound = Assert.IsType<CompoundQueryExpression>(result);
        Assert.Equal(SetOperationType.Except, compound.OperationType);
        Assert.False(compound.All);
    }

    [Fact]
    public void Parse_IntersectBindsTighterThanUnion()
    {
        // "A UNION B INTERSECT C" → "A UNION (B INTERSECT C)"
        QueryExpression result = SqlParser.Parse(
            "SELECT a FROM t1 UNION SELECT a FROM t2 INTERSECT SELECT a FROM t3");

        CompoundQueryExpression union = Assert.IsType<CompoundQueryExpression>(result);
        Assert.Equal(SetOperationType.Union, union.OperationType);

        Assert.IsType<SelectQueryExpression>(union.Left);

        CompoundQueryExpression intersect = Assert.IsType<CompoundQueryExpression>(union.Right);
        Assert.Equal(SetOperationType.Intersect, intersect.OperationType);
    }

    [Fact]
    public void Parse_ChainedUnionsAreLeftAssociative()
    {
        // "A UNION B UNION C" → "(A UNION B) UNION C"
        QueryExpression result = SqlParser.Parse(
            "SELECT a FROM t1 UNION SELECT a FROM t2 UNION SELECT a FROM t3");

        CompoundQueryExpression outer = Assert.IsType<CompoundQueryExpression>(result);
        Assert.Equal(SetOperationType.Union, outer.OperationType);
        Assert.IsType<SelectQueryExpression>(outer.Right);

        CompoundQueryExpression inner = Assert.IsType<CompoundQueryExpression>(outer.Left);
        Assert.Equal(SetOperationType.Union, inner.OperationType);
    }

    [Fact]
    public void Parse_CompoundWithTrailingOrderBy()
    {
        QueryExpression result = SqlParser.Parse(
            "SELECT a FROM t1 UNION SELECT a FROM t2 ORDER BY a");

        CompoundQueryExpression compound = Assert.IsType<CompoundQueryExpression>(result);
        Assert.NotNull(compound.OrderBy);
        Assert.Single(compound.OrderBy.Items);
    }

    [Fact]
    public void Parse_CompoundWithTrailingLimit()
    {
        QueryExpression result = SqlParser.Parse(
            "SELECT a FROM t1 UNION SELECT a FROM t2 LIMIT 10");

        CompoundQueryExpression compound = Assert.IsType<CompoundQueryExpression>(result);
        Assert.NotNull(compound.Limit);
    }

    [Fact]
    public void Parse_SimpleSelectReturnsSelectQueryExpression()
    {
        QueryExpression result = SqlParser.Parse("SELECT a FROM t");

        Assert.IsType<SelectQueryExpression>(result);
    }

    // ─────────────── UNION ALL ───────────────

    [Fact]
    public async Task UnionAll_ConcatenatesBothSources()
    {
        MockOperator left = CreateMockOperator(XColumns, [1f], [2f]);
        MockOperator right = CreateMockOperator(XColumns, [3f], [4f]);
        SetOperationOperator op = new(left, right, SetOperationType.Union, all: true);

        List<Row> results = await CollectAsync(op);

        Assert.Equal(4, results.Count);
        Assert.Equal(1f, results[0][0].AsFloat32());
        Assert.Equal(2f, results[1][0].AsFloat32());
        Assert.Equal(3f, results[2][0].AsFloat32());
        Assert.Equal(4f, results[3][0].AsFloat32());
    }

    [Fact]
    public async Task UnionAll_PreservesDuplicates()
    {
        MockOperator left = CreateMockOperator(XColumns, [1f], [2f], [1f]);
        MockOperator right = CreateMockOperator(XColumns, [2f], [3f]);
        SetOperationOperator op = new(left, right, SetOperationType.Union, all: true);

        List<Row> results = await CollectAsync(op);

        Assert.Equal(5, results.Count);
    }

    [Fact]
    public async Task UnionAll_EmptyLeft()
    {
        MockOperator left = CreateMockOperator(XColumns);
        MockOperator right = CreateMockOperator(XColumns, [1f], [2f]);
        SetOperationOperator op = new(left, right, SetOperationType.Union, all: true);

        List<Row> results = await CollectAsync(op);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task UnionAll_EmptyRight()
    {
        MockOperator left = CreateMockOperator(XColumns, [1f], [2f]);
        MockOperator right = CreateMockOperator(XColumns);
        SetOperationOperator op = new(left, right, SetOperationType.Union, all: true);

        List<Row> results = await CollectAsync(op);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task UnionAll_BothEmpty()
    {
        MockOperator left = CreateMockOperator(XColumns);
        MockOperator right = CreateMockOperator(XColumns);
        SetOperationOperator op = new(left, right, SetOperationType.Union, all: true);

        List<Row> results = await CollectAsync(op);

        Assert.Empty(results);
    }

    // ─────────────── UNION DISTINCT ───────────────

    [Fact]
    public async Task UnionDistinct_RemovesDuplicates()
    {
        MockOperator left = CreateMockOperator(XColumns, [1f], [2f], [1f]);
        MockOperator right = CreateMockOperator(XColumns, [2f], [3f]);
        SetOperationOperator op = new(left, right, SetOperationType.Union, all: false);

        List<Row> results = await CollectAsync(op);

        Assert.Equal(3, results.Count);
        float[] values = results.Select(row => row[0].AsFloat32()).OrderBy(value => value).ToArray();
        Assert.Equal([1f, 2f, 3f], values);
    }

    [Fact]
    public async Task UnionDistinct_AllDuplicates()
    {
        MockOperator left = CreateMockOperator(XColumns, [1f], [1f]);
        MockOperator right = CreateMockOperator(XColumns, [1f], [1f]);
        SetOperationOperator op = new(left, right, SetOperationType.Union, all: false);

        List<Row> results = await CollectAsync(op);

        Assert.Single(results);
    }

    [Fact]
    public async Task UnionDistinct_MultiColumn()
    {
        MockOperator left = CreateMockOperator(["a", "b"], [1f, "x"], [2f, "y"]);
        MockOperator right = CreateMockOperator(["a", "b"], [1f, "x"]);
        SetOperationOperator op = new(left, right, SetOperationType.Union, all: false);

        List<Row> results = await CollectAsync(op);

        Assert.Equal(2, results.Count);
    }

    // ─────────────── INTERSECT DISTINCT ───────────────

    [Fact]
    public async Task IntersectDistinct_ReturnsCommonRows()
    {
        MockOperator left = CreateMockOperator(XColumns, [1f], [2f], [3f]);
        MockOperator right = CreateMockOperator(XColumns, [2f], [3f], [4f]);
        SetOperationOperator op = new(left, right, SetOperationType.Intersect, all: false);

        List<Row> results = await CollectAsync(op);

        Assert.Equal(2, results.Count);
        float[] values = results.Select(row => row[0].AsFloat32()).OrderBy(value => value).ToArray();
        Assert.Equal([2f, 3f], values);
    }

    [Fact]
    public async Task IntersectDistinct_NoOverlap()
    {
        MockOperator left = CreateMockOperator(XColumns, [1f], [2f]);
        MockOperator right = CreateMockOperator(XColumns, [3f], [4f]);
        SetOperationOperator op = new(left, right, SetOperationType.Intersect, all: false);

        List<Row> results = await CollectAsync(op);

        Assert.Empty(results);
    }

    [Fact]
    public async Task IntersectDistinct_EmptyRight()
    {
        MockOperator left = CreateMockOperator(XColumns, [1f], [2f]);
        MockOperator right = CreateMockOperator(XColumns);
        SetOperationOperator op = new(left, right, SetOperationType.Intersect, all: false);

        List<Row> results = await CollectAsync(op);

        Assert.Empty(results);
    }

    [Fact]
    public async Task IntersectDistinct_DuplicatesInLeftYieldOnce()
    {
        MockOperator left = CreateMockOperator(XColumns, [1f], [1f], [2f]);
        MockOperator right = CreateMockOperator(XColumns, [1f], [2f]);
        SetOperationOperator op = new(left, right, SetOperationType.Intersect, all: false);

        List<Row> results = await CollectAsync(op);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task IntersectDistinct_WithSpill()
    {
        // 0..499 ∩ 250..749 → 250..499 = 250 distinct values. Tight budget forces
        // partition spill on both sides.
        object?[][] leftRows = Enumerable.Range(0, 500).Select(index => new object?[] { (float)index }).ToArray();
        object?[][] rightRows = Enumerable.Range(250, 500).Select(index => new object?[] { (float)index }).ToArray();
        MockOperator left = CreateMockOperator(XColumns, rows: leftRows);
        MockOperator right = CreateMockOperator(XColumns, rows: rightRows);
        SetOperationOperator op = new(left, right, SetOperationType.Intersect, all: false);

        ExecutionContext context = CreateExecutionContext(memoryBudgetBytes: 1024);
        List<Row> results = await CollectAsync(op, context);

        Assert.Equal(250, results.Count);
        Assert.True(op.SpillingTriggered, "Expected the budget to trigger spill so this test exercises the spill path.");

        float[] values = results.Select(row => row[0].AsFloat32()).OrderBy(value => value).ToArray();
        float[] expected = Enumerable.Range(250, 250).Select(index => (float)index).ToArray();
        Assert.Equal(expected, values);

        op.Dispose();
    }

    [Fact]
    public async Task IntersectDistinct_SingleColumn_NoSpill_PoolDoesNotLeak()
    {
        Pool pool = GetService<Pool>();
        MockOperator left = CreateMockOperator(XColumns, [1f], [2f], [3f]);
        MockOperator right = CreateMockOperator(XColumns, [2f], [3f], [4f]);
        SetOperationOperator op = new(left, right, SetOperationType.Intersect, all: false);

        List<Row> results = await CollectAsync(op);
        op.Dispose();

        Assert.Equal(2, results.Count);
        AssertPoolBalanced(pool);
    }

    [Fact]
    public async Task IntersectDistinct_MultiColumn_NoSpill_PoolDoesNotLeak()
    {
        Pool pool = GetService<Pool>();
        MockOperator left = CreateMockOperator(["a", "b"], [1f, "x"], [2f, "y"], [3f, "z"]);
        MockOperator right = CreateMockOperator(["a", "b"], [2f, "y"], [3f, "z"], [4f, "w"]);
        SetOperationOperator op = new(left, right, SetOperationType.Intersect, all: false);

        List<Row> results = await CollectAsync(op);
        op.Dispose();

        Assert.Equal(2, results.Count);
        AssertPoolBalanced(pool);
    }

    [Fact]
    public async Task IntersectDistinct_WithSpill_PoolDoesNotLeak()
    {
        Pool pool = GetService<Pool>();
        object?[][] leftRows = Enumerable.Range(0, 500).Select(index => new object?[] { (float)index }).ToArray();
        object?[][] rightRows = Enumerable.Range(250, 500).Select(index => new object?[] { (float)index }).ToArray();
        MockOperator left = CreateMockOperator(XColumns, rows: leftRows);
        MockOperator right = CreateMockOperator(XColumns, rows: rightRows);
        SetOperationOperator op = new(left, right, SetOperationType.Intersect, all: false);

        ExecutionContext context = CreateExecutionContext(memoryBudgetBytes: 1024);
        List<Row> results = await CollectAsync(op, context);

        Assert.Equal(250, results.Count);
        Assert.True(op.SpillingTriggered, "Expected the budget to trigger spill so the leak check covers spill paths.");

        op.Dispose();
        AssertPoolBalanced(pool);
    }

    // ─────────────── INTERSECT ALL ───────────────

    [Fact]
    public async Task IntersectAll_ReturnsMinimumOccurrences()
    {
        // Left has 1 three times, 2 once. Right has 1 twice, 2 twice.
        // Result: 1 twice, 2 once.
        MockOperator left = CreateMockOperator(XColumns, [1f], [1f], [1f], [2f]);
        MockOperator right = CreateMockOperator(XColumns, [1f], [1f], [2f], [2f]);
        SetOperationOperator op = new(left, right, SetOperationType.Intersect, all: true);

        List<Row> results = await CollectAsync(op);

        Assert.Equal(3, results.Count);
        int onesCount = results.Count(row => row[0].AsFloat32() == 1f);
        int twosCount = results.Count(row => row[0].AsFloat32() == 2f);
        Assert.Equal(2, onesCount);
        Assert.Equal(1, twosCount);
    }

    [Fact]
    public async Task IntersectAll_NoOverlap()
    {
        MockOperator left = CreateMockOperator(XColumns, [1f]);
        MockOperator right = CreateMockOperator(XColumns, [2f]);
        SetOperationOperator op = new(left, right, SetOperationType.Intersect, all: true);

        List<Row> results = await CollectAsync(op);

        Assert.Empty(results);
    }

    // ─────────────── EXCEPT DISTINCT ───────────────

    [Fact]
    public async Task ExceptDistinct_RemovesRightRows()
    {
        MockOperator left = CreateMockOperator(XColumns, [1f], [2f], [3f]);
        MockOperator right = CreateMockOperator(XColumns, [2f], [4f]);
        SetOperationOperator op = new(left, right, SetOperationType.Except, all: false);

        List<Row> results = await CollectAsync(op);

        Assert.Equal(2, results.Count);
        float[] values = results.Select(row => row[0].AsFloat32()).OrderBy(value => value).ToArray();
        Assert.Equal([1f, 3f], values);
    }

    [Fact]
    public async Task ExceptDistinct_EmptyRight_ReturnsDistinctLeft()
    {
        MockOperator left = CreateMockOperator(XColumns, [1f], [1f], [2f]);
        MockOperator right = CreateMockOperator(XColumns);
        SetOperationOperator op = new(left, right, SetOperationType.Except, all: false);

        List<Row> results = await CollectAsync(op);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task ExceptDistinct_AllRemoved()
    {
        MockOperator left = CreateMockOperator(XColumns, [1f], [2f]);
        MockOperator right = CreateMockOperator(XColumns, [1f], [2f]);
        SetOperationOperator op = new(left, right, SetOperationType.Except, all: false);

        List<Row> results = await CollectAsync(op);

        Assert.Empty(results);
    }

    [Fact]
    public async Task ExceptDistinct_DuplicatesInLeftStillSingle()
    {
        MockOperator left = CreateMockOperator(XColumns, [1f], [1f], [1f]);
        MockOperator right = CreateMockOperator(XColumns, [2f]);
        SetOperationOperator op = new(left, right, SetOperationType.Except, all: false);

        List<Row> results = await CollectAsync(op);

        // EXCEPT DISTINCT returns each row at most once.
        Assert.Single(results);
    }

    // ─────────────── EXCEPT ALL ───────────────

    [Fact]
    public async Task ExceptAll_SubtractsCounts()
    {
        // Left has 1 three times, 2 once. Right has 1 once.
        // Result: 1 twice, 2 once.
        MockOperator left = CreateMockOperator(XColumns, [1f], [1f], [1f], [2f]);
        MockOperator right = CreateMockOperator(XColumns, [1f]);
        SetOperationOperator op = new(left, right, SetOperationType.Except, all: true);

        List<Row> results = await CollectAsync(op);

        Assert.Equal(3, results.Count);
        int onesCount = results.Count(row => row[0].AsFloat32() == 1f);
        Assert.Equal(2, onesCount);
    }

    [Fact]
    public async Task ExceptAll_EmptyRight_ReturnsAllLeft()
    {
        MockOperator left = CreateMockOperator(XColumns, [1f], [1f], [2f]);
        MockOperator right = CreateMockOperator(XColumns);
        SetOperationOperator op = new(left, right, SetOperationType.Except, all: true);

        List<Row> results = await CollectAsync(op);

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task ExceptAll_MoreInRightThanLeft()
    {
        MockOperator left = CreateMockOperator(XColumns, [1f]);
        MockOperator right = CreateMockOperator(XColumns, [1f], [1f], [1f]);
        SetOperationOperator op = new(left, right, SetOperationType.Except, all: true);

        List<Row> results = await CollectAsync(op);

        Assert.Empty(results);
    }

    // ─────────────── Edge cases ───────────────

    [Fact]
    public async Task UnionAll_LargeDataset()
    {
        object?[][] leftRows = Enumerable.Range(0, 1000).Select(index => new object?[] { (float)index }).ToArray();
        object?[][] rightRows = Enumerable.Range(500, 1000).Select(index => new object?[] { (float)index }).ToArray();
        MockOperator left = CreateMockOperator(XColumns, rows: leftRows);
        MockOperator right = CreateMockOperator(XColumns, rows: rightRows);
        SetOperationOperator op = new(left, right, SetOperationType.Union, all: true);

        List<Row> results = await CollectAsync(op);

        Assert.Equal(2000, results.Count);
    }

    [Fact]
    public async Task UnionDistinct_WithSpill()
    {
        // Use a very small memory budget to force spill-to-disk.
        object?[][] leftRows = Enumerable.Range(0, 500).Select(index => new object?[] { (float)index }).ToArray();
        object?[][] rightRows = Enumerable.Range(250, 500).Select(index => new object?[] { (float)index }).ToArray();
        MockOperator left = CreateMockOperator(XColumns, rows: leftRows);
        MockOperator right = CreateMockOperator(XColumns, rows: rightRows);
        SetOperationOperator op = new(left, right, SetOperationType.Union, all: false);

        ExecutionContext context = CreateExecutionContext(memoryBudgetBytes: 1024);
        List<Row> results = await CollectAsync(op, context);

        // 0..499 UNION 250..749 → 0..749 = 750 distinct values
        Assert.Equal(750, results.Count);
        Assert.True(op.SpillingTriggered, "Expected the budget to trigger spill so this test exercises the spill path.");

        float[] values = results.Select(row => row[0].AsFloat32()).OrderBy(value => value).ToArray();
        float[] expected = Enumerable.Range(0, 750).Select(index => (float)index).ToArray();
        Assert.Equal(expected, values);

        op.Dispose();
    }

    /// <summary>
    /// Exposes the Option-A algorithm bug: the in-memory hash set grows unbounded even
    /// after spill triggers, because every new row is added to the set + emitted
    /// immediately + also written to a spill partition. The drain phase then seeds its
    /// partition-local set from the in-memory set and finds every spilled key already
    /// present, so it emits nothing — the spill machinery is dead code. Output is still
    /// correct, but multi-tenant memory safety is not delivered.
    /// </summary>
    /// <remarks>
    /// Once fixed (route post-spill rows to spill-only and let drain emit them), this
    /// test passes because <c>DrainEmittedRowCount</c> is non-zero.
    /// </remarks>
    [Fact]
    public async Task UnionDistinct_AfterSpill_DrainEmitsSpilledRows()
    {
        object?[][] leftRows = Enumerable.Range(0, 500).Select(index => new object?[] { (float)index }).ToArray();
        MockOperator left = CreateMockOperator(XColumns, rows: leftRows);
        MockOperator right = CreateMockOperator(XColumns);
        SetOperationOperator op = new(left, right, SetOperationType.Union, all: false);

        ExecutionContext context = CreateExecutionContext(memoryBudgetBytes: 1024);
        List<Row> results = await CollectAsync(op, context);

        Assert.Equal(500, results.Count);

        Assert.True(
            op.DrainEmittedRowCount > 0,
            $"Expected drain phase to emit spilled rows after budget exceeded, but emitted "
            + $"{op.DrainEmittedRowCount}. The in-memory hash set absorbed every row, so the "
            + $"spill machinery is dead code and memory growth is unbounded.");

        op.Dispose();
    }

    // ─────────────── POOL LEAK CHECKS ───────────────

    /// <summary>
    /// After a single-column UNION DISTINCT with no spill, every rent on the test's
    /// pool must have a matching return: DataValue[]s, RowBatches, and Arenas.
    /// Catches output-batch leaks, hashSetArena leaks, and missing per-row returns.
    /// </summary>
    [Fact]
    public async Task UnionDistinct_SingleColumn_NoSpill_PoolDoesNotLeak()
    {
        Pool pool = GetService<Pool>();
        MockOperator left = CreateMockOperator(XColumns, [1f], [2f], [1f]);
        MockOperator right = CreateMockOperator(XColumns, [2f], [3f]);
        SetOperationOperator op = new(left, right, SetOperationType.Union, all: false);

        List<Row> results = await CollectAsync(op);
        op.Dispose();

        Assert.Equal(3, results.Count);
        AssertPoolBalanced(pool);
    }

    /// <summary>
    /// Multi-column UNION DISTINCT with no spill exercises the composite-key path,
    /// which rents <c>compositeKeyScratch</c> from the pool. The scratch buffer must
    /// be returned in the operator's finally block.
    /// </summary>
    [Fact]
    public async Task UnionDistinct_MultiColumn_NoSpill_PoolDoesNotLeak()
    {
        Pool pool = GetService<Pool>();
        MockOperator left = CreateMockOperator(["a", "b"], [1f, "x"], [2f, "y"], [1f, "x"]);
        MockOperator right = CreateMockOperator(["a", "b"], [2f, "y"], [3f, "z"]);
        SetOperationOperator op = new(left, right, SetOperationType.Union, all: false);

        List<Row> results = await CollectAsync(op);
        op.Dispose();

        Assert.Equal(3, results.Count);
        AssertPoolBalanced(pool);
    }

    /// <summary>
    /// UNION DISTINCT with a tight memory budget forces spill, exercising the spiller's
    /// per-partition buffers, the file-backed consolidated arena, and the drain phase.
    /// All of those must return to the pool by the time iteration completes + Dispose runs.
    /// </summary>
    [Fact]
    public async Task UnionDistinct_WithSpill_PoolDoesNotLeak()
    {
        Pool pool = GetService<Pool>();
        object?[][] leftRows = Enumerable.Range(0, 500).Select(index => new object?[] { (float)index }).ToArray();
        object?[][] rightRows = Enumerable.Range(250, 500).Select(index => new object?[] { (float)index }).ToArray();
        MockOperator left = CreateMockOperator(XColumns, rows: leftRows);
        MockOperator right = CreateMockOperator(XColumns, rows: rightRows);
        SetOperationOperator op = new(left, right, SetOperationType.Union, all: false);

        ExecutionContext context = CreateExecutionContext(memoryBudgetBytes: 1024);
        List<Row> results = await CollectAsync(op, context);

        Assert.Equal(750, results.Count);
        Assert.True(op.SpillingTriggered, "Expected the budget to trigger spill so the leak check covers spill paths.");

        op.Dispose();
        AssertPoolBalanced(pool);
    }

    /// <summary>
    /// Asserts that every rent on this pool has a matching return — the "no leak"
    /// invariant. Run after the operator and any owned RowBatches have been
    /// disposed/returned.
    /// </summary>
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

    [Fact]
    public void Properties_AreExposed()
    {
        MockOperator left = CreateMockOperator(XColumns);
        MockOperator right = CreateMockOperator(XColumns);
        SetOperationOperator op = new(left, right, SetOperationType.Intersect, all: true);

        Assert.Same(left, op.Left);
        Assert.Same(right, op.Right);
        Assert.Equal(SetOperationType.Intersect, op.OperationType);
        Assert.True(op.All);
    }
}
