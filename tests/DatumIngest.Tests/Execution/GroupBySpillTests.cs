using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Functions.Aggregates;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for <see cref="GroupByOperator"/> spill-to-disk behaviour
/// when <see cref="ExecutionContext.MemoryBudgetBytes"/> is set.
/// </summary>
public sealed class GroupBySpillTests : ServiceTestBase
{
    /// <summary>Tiny memory budget that forces spilling for even a few rows.</summary>
    private const long TinyBudget = 256;

    // ─────────────── Helpers ───────────────

    private ExecutionContext CreateContext(long? memoryBudgetBytes = null)
    {
        return CreateExecutionContext(memoryBudgetBytes: memoryBudgetBytes);
    }

    private static readonly string[] KeyValColumns = ["key", "val"];

    /// <summary>
    /// Builds 100 groups × 5 rows = 500 rows as raw <c>object?[]</c> tuples.
    /// </summary>
    private static object?[][] BuildManyGroupRows()
    {
        object?[][] rows = new object?[500][];
        int index = 0;

        for (int groupIndex = 0; groupIndex < 100; groupIndex++)
        {
            for (int rowIndex = 1; rowIndex <= 5; rowIndex++)
            {
                rows[index++] = [$"G{groupIndex}", (float)rowIndex];
            }
        }

        return rows;
    }

    private static async Task<List<Row>> CollectAsync(IQueryOperator op, ExecutionContext context)
    {
        return await op.CollectRowsAsync(context);
    }

    // ─────────────── Single-key spill ───────────────

    /// <summary>
    /// Single-key GROUP BY with COUNT(*) and SUM produces correct results
    /// even when forced to spill to disk.
    /// </summary>
    [Fact]
    public async Task SingleKey_WithSpill_CountAndSum()
    {
        MockOperator source = CreateMockOperator(
            ["category", "value"],
            ["A", 1f],
            ["B", 2f],
            ["A", 3f],
            ["B", 4f],
            ["A", 5f],
            ["C", 6f],
            ["C", 7f],
            ["A", 8f],
            ["B", 9f],
            ["C", 10f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("category")],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
                new AggregateColumn(
                    new SumFunction(),
                    [new ColumnReference("value")],
                    "SUM(value)"),
            ]);

        List<Row> results = await CollectAsync(groupBy, CreateContext(TinyBudget));

        Assert.Equal(3, results.Count);

        Row groupA = results.First(row => row["category"].AsString() == "A");
        Row groupB = results.First(row => row["category"].AsString() == "B");
        Row groupC = results.First(row => row["category"].AsString() == "C");

        Assert.Equal(4L, groupA["COUNT(*)"].AsInt64());
        Assert.Equal(17f, groupA["SUM(value)"].AsFloat32());

        Assert.Equal(3L, groupB["COUNT(*)"].AsInt64());
        Assert.Equal(15f, groupB["SUM(value)"].AsFloat32());

        Assert.Equal(3L, groupC["COUNT(*)"].AsInt64());
        Assert.Equal(23f, groupC["SUM(value)"].AsFloat32());
    }

    /// <summary>
    /// Many groups ensure multiple spill partitions are used and re-aggregated.
    /// </summary>
    [Fact]
    public async Task SingleKey_ManyGroups_SpillMatchesUnbounded()
    {
        // Build 100 groups with 5 rows each = 500 rows.
        // Each operator gets its own copy so data returned to GlobalBufferPool
        // by the first operator cannot corrupt the second operator's input.
        object?[][] spillRows = BuildManyGroupRows();
        object?[][] unboundedRows = BuildManyGroupRows();

        GroupByOperator spillGroupBy = new(
            CreateMockOperator(KeyValColumns, spillRows),
            groupByExpressions: [new ColumnReference("key")],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
                new AggregateColumn(
                    new SumFunction(),
                    [new ColumnReference("val")],
                    "SUM(val)"),
            ]);

        GroupByOperator unboundedGroupBy = new(
            CreateMockOperator(KeyValColumns, unboundedRows),
            groupByExpressions: [new ColumnReference("key")],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
                new AggregateColumn(
                    new SumFunction(),
                    [new ColumnReference("val")],
                    "SUM(val)"),
            ]);

        List<Row> spillResults = await CollectAsync(spillGroupBy, CreateContext(TinyBudget));
        List<Row> unboundedResults = await CollectAsync(unboundedGroupBy, CreateContext());

        Assert.Equal(unboundedResults.Count, spillResults.Count);
        Assert.Equal(100, spillResults.Count);

        // Verify every group matches.
        Dictionary<string, (long Count, float Sum)> expected = unboundedResults.ToDictionary(
            row => row["key"].AsString(),
            row => (row["COUNT(*)"].AsInt64(), row["SUM(val)"].AsFloat32()));

        foreach (Row row in spillResults)
        {
            string key = row["key"].AsString();
            Assert.True(expected.ContainsKey(key), $"Unexpected group key: {key}");
            Assert.Equal(expected[key].Count, row["COUNT(*)"].AsInt64());
            Assert.Equal(expected[key].Sum, row["SUM(val)"].AsFloat32());
        }
    }

    // ─────────────── Composite-key spill ───────────────

    /// <summary>
    /// Composite-key GROUP BY with spill produces correct results.
    /// </summary>
    [Fact]
    public async Task CompositeKey_WithSpill_ProducesCorrectResults()
    {
        MockOperator source = CreateMockOperator(
            ["dept", "status", "amount"],
            ["X", "active", 100f],
            ["X", "inactive", 200f],
            ["X", "active", 300f],
            ["Y", "active", 50f],
            ["Y", "inactive", 75f],
            ["X", "inactive", 150f],
            ["Y", "active", 25f],
            ["X", "active", 500f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("dept"), new ColumnReference("status")],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
                new AggregateColumn(
                    new SumFunction(),
                    [new ColumnReference("amount")],
                    "SUM(amount)"),
            ]);

        List<Row> results = await CollectAsync(groupBy, CreateContext(TinyBudget));

        Assert.Equal(4, results.Count);

        Row groupXActive = results.First(row =>
            row["dept"].AsString() == "X" && row["status"].AsString() == "active");
        Assert.Equal(3L, groupXActive["COUNT(*)"].AsInt64());
        Assert.Equal(900f, groupXActive["SUM(amount)"].AsFloat32());

        Row groupXInactive = results.First(row =>
            row["dept"].AsString() == "X" && row["status"].AsString() == "inactive");
        Assert.Equal(2L, groupXInactive["COUNT(*)"].AsInt64());
        Assert.Equal(350f, groupXInactive["SUM(amount)"].AsFloat32());

        Row groupYActive = results.First(row =>
            row["dept"].AsString() == "Y" && row["status"].AsString() == "active");
        Assert.Equal(2L, groupYActive["COUNT(*)"].AsInt64());
        Assert.Equal(75f, groupYActive["SUM(amount)"].AsFloat32());

        Row groupYInactive = results.First(row =>
            row["dept"].AsString() == "Y" && row["status"].AsString() == "inactive");
        Assert.Equal(1L, groupYInactive["COUNT(*)"].AsInt64());
        Assert.Equal(75f, groupYInactive["SUM(amount)"].AsFloat32());
    }

    // ─────────────── Global aggregation is never spilled ───────────────

    /// <summary>
    /// Global aggregation (no GROUP BY) with a tiny budget still works —
    /// global aggregation only uses a single group and never triggers spill.
    /// </summary>
    [Fact]
    public async Task GlobalAggregation_WithBudget_StillWorks()
    {
        MockOperator source = CreateMockOperator(["x"], [1f], [2f], [3f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
                new AggregateColumn(
                    new SumFunction(),
                    [new ColumnReference("x")],
                    "SUM(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy, CreateContext(TinyBudget));

        Assert.Single(results);
        Assert.Equal(3L, results[0]["COUNT(*)"].AsInt64());
        Assert.Equal(6f, results[0]["SUM(x)"].AsFloat32());
    }

    // ─────────────── No budget = in-memory (no spill) ───────────────

    /// <summary>
    /// Without a memory budget, GroupByOperator works fully in-memory
    /// (same as original behavior).
    /// </summary>
    [Fact]
    public async Task NoBudget_InMemory_ProducesCorrectResults()
    {
        MockOperator source = CreateMockOperator(
            KeyValColumns,
            ["A", 10f],
            ["B", 20f],
            ["A", 30f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("key")],
            aggregateColumns:
            [
                new AggregateColumn(
                    new SumFunction(),
                    [new ColumnReference("val")],
                    "SUM(val)"),
            ]);

        List<Row> results = await CollectAsync(groupBy, CreateContext());

        Assert.Equal(2, results.Count);

        Row groupA = results.First(row => row["key"].AsString() == "A");
        Assert.Equal(40f, groupA["SUM(val)"].AsFloat32());

        Row groupB = results.First(row => row["key"].AsString() == "B");
        Assert.Equal(20f, groupB["SUM(val)"].AsFloat32());
    }

    // ─────────────── MIN / MAX with spill ───────────────

    /// <summary>
    /// MIN and MAX produce correct results when spill is triggered.
    /// </summary>
    [Fact]
    public async Task MinMax_WithSpill()
    {
        MockOperator source = CreateMockOperator(
            ["grp", "v"],
            ["G1", 5f],
            ["G1", 15f],
            ["G1", 10f],
            ["G2", 100f],
            ["G2", 50f],
            ["G2", 75f],
            ["G1", 1f],
            ["G2", 200f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("grp")],
            aggregateColumns:
            [
                new AggregateColumn(
                    new MinFunction(),
                    [new ColumnReference("v")],
                    "MIN(v)"),
                new AggregateColumn(
                    new MaxFunction(),
                    [new ColumnReference("v")],
                    "MAX(v)"),
            ]);

        List<Row> results = await CollectAsync(groupBy, CreateContext(TinyBudget));

        Assert.Equal(2, results.Count);

        Row g1 = results.First(row => row["grp"].AsString() == "G1");
        Assert.Equal(1f, g1["MIN(v)"].AsFloat32());
        Assert.Equal(15f, g1["MAX(v)"].AsFloat32());

        Row g2 = results.First(row => row["grp"].AsString() == "G2");
        Assert.Equal(50f, g2["MIN(v)"].AsFloat32());
        Assert.Equal(200f, g2["MAX(v)"].AsFloat32());
    }

    // ─────────────── Dispose cleanup ───────────────

    /// <summary>
    /// Dispose cleans up the spill directory without throwing.
    /// </summary>
    [Fact]
    public async Task Dispose_CleansUpSpillDirectory()
    {
        MockOperator source = CreateMockOperator(
            KeyValColumns,
            ["A", 1f],
            ["B", 2f],
            ["A", 3f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("key")],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
            ]);

        // Execute with budget to trigger spill setup.
        List<Row> results = await CollectAsync(groupBy, CreateContext(TinyBudget));

        // Dispose should not throw even after normal execution.
        groupBy.Dispose();
        groupBy.Dispose(); // Double dispose is safe.
    }

    // ─────────────── Empty source ───────────────

    /// <summary>
    /// Empty source with GROUP BY and budget produces no rows.
    /// </summary>
    [Fact]
    public async Task EmptySource_WithBudget_ProducesNoRows()
    {
        MockOperator source = CreateMockOperator(KeyValColumns);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("key")],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
            ]);

        List<Row> results = await CollectAsync(groupBy, CreateContext(TinyBudget));

        Assert.Empty(results);
    }
}
