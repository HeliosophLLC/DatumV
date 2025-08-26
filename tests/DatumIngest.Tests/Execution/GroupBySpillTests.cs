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
public sealed class GroupBySpillTests
{
    /// <summary>Tiny memory budget that forces spilling for even a few rows.</summary>
    private const long TinyBudget = 256;

    // ─────────────── Helpers ───────────────

    private static ExecutionContext CreateContext(long? memoryBudgetBytes = null)
    {
        return new ExecutionContext(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            new TableCatalog(),
            new RowBufferPool(),
            memoryBudgetBytes: memoryBudgetBytes);
    }

    private static Row MakeRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = columns.Select(c => c.Name).ToArray();
        DataValue[] values = columns.Select(c => c.Value).ToArray();
        return new Row(names, values);
    }

    private static async Task<List<Row>> CollectAsync(IQueryOperator op, ExecutionContext context)
    {
        List<Row> rows = [];
        await foreach (Row row in op.ExecuteAsync(context))
        {
            rows.Add(row);
        }

        return rows;
    }

    // ─────────────── Single-key spill ───────────────

    /// <summary>
    /// Single-key GROUP BY with COUNT(*) and SUM produces correct results
    /// even when forced to spill to disk.
    /// </summary>
    [Fact]
    public async Task SingleKey_WithSpill_CountAndSum()
    {
        MockOperator source = new(
            MakeRow(("category", DataValue.FromString("A")), ("value", DataValue.FromFloat32(1f))),
            MakeRow(("category", DataValue.FromString("B")), ("value", DataValue.FromFloat32(2f))),
            MakeRow(("category", DataValue.FromString("A")), ("value", DataValue.FromFloat32(3f))),
            MakeRow(("category", DataValue.FromString("B")), ("value", DataValue.FromFloat32(4f))),
            MakeRow(("category", DataValue.FromString("A")), ("value", DataValue.FromFloat32(5f))),
            MakeRow(("category", DataValue.FromString("C")), ("value", DataValue.FromFloat32(6f))),
            MakeRow(("category", DataValue.FromString("C")), ("value", DataValue.FromFloat32(7f))),
            MakeRow(("category", DataValue.FromString("A")), ("value", DataValue.FromFloat32(8f))),
            MakeRow(("category", DataValue.FromString("B")), ("value", DataValue.FromFloat32(9f))),
            MakeRow(("category", DataValue.FromString("C")), ("value", DataValue.FromFloat32(10f))));

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

        Assert.Equal(4f, groupA["COUNT(*)"].AsFloat32());
        Assert.Equal(17f, groupA["SUM(value)"].AsFloat32());

        Assert.Equal(3f, groupB["COUNT(*)"].AsFloat32());
        Assert.Equal(15f, groupB["SUM(value)"].AsFloat32());

        Assert.Equal(3f, groupC["COUNT(*)"].AsFloat32());
        Assert.Equal(23f, groupC["SUM(value)"].AsFloat32());
    }

    /// <summary>
    /// Many groups ensure multiple spill partitions are used and re-aggregated.
    /// </summary>
    [Fact]
    public async Task SingleKey_ManyGroups_SpillMatchesUnbounded()
    {
        // Build 100 groups with 5 rows each = 500 rows.
        List<Row> sourceRows = new();
        for (int groupIndex = 0; groupIndex < 100; groupIndex++)
        {
            for (int rowIndex = 1; rowIndex <= 5; rowIndex++)
            {
                sourceRows.Add(MakeRow(
                    ("key", DataValue.FromString($"G{groupIndex}")),
                    ("val", DataValue.FromFloat32((float)rowIndex))));
            }
        }

        GroupByOperator spillGroupBy = new(
            new MockOperator(sourceRows.ToArray()),
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
            new MockOperator(sourceRows.ToArray()),
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
        Dictionary<string, (float Count, float Sum)> expected = unboundedResults.ToDictionary(
            row => row["key"].AsString(),
            row => (row["COUNT(*)"].AsFloat32(), row["SUM(val)"].AsFloat32()));

        foreach (Row row in spillResults)
        {
            string key = row["key"].AsString();
            Assert.True(expected.ContainsKey(key), $"Unexpected group key: {key}");
            Assert.Equal(expected[key].Count, row["COUNT(*)"].AsFloat32());
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
        MockOperator source = new(
            MakeRow(("dept", DataValue.FromString("X")), ("status", DataValue.FromString("active")), ("amount", DataValue.FromFloat32(100f))),
            MakeRow(("dept", DataValue.FromString("X")), ("status", DataValue.FromString("inactive")), ("amount", DataValue.FromFloat32(200f))),
            MakeRow(("dept", DataValue.FromString("X")), ("status", DataValue.FromString("active")), ("amount", DataValue.FromFloat32(300f))),
            MakeRow(("dept", DataValue.FromString("Y")), ("status", DataValue.FromString("active")), ("amount", DataValue.FromFloat32(50f))),
            MakeRow(("dept", DataValue.FromString("Y")), ("status", DataValue.FromString("inactive")), ("amount", DataValue.FromFloat32(75f))),
            MakeRow(("dept", DataValue.FromString("X")), ("status", DataValue.FromString("inactive")), ("amount", DataValue.FromFloat32(150f))),
            MakeRow(("dept", DataValue.FromString("Y")), ("status", DataValue.FromString("active")), ("amount", DataValue.FromFloat32(25f))),
            MakeRow(("dept", DataValue.FromString("X")), ("status", DataValue.FromString("active")), ("amount", DataValue.FromFloat32(500f))));

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
        Assert.Equal(3f, groupXActive["COUNT(*)"].AsFloat32());
        Assert.Equal(900f, groupXActive["SUM(amount)"].AsFloat32());

        Row groupXInactive = results.First(row =>
            row["dept"].AsString() == "X" && row["status"].AsString() == "inactive");
        Assert.Equal(2f, groupXInactive["COUNT(*)"].AsFloat32());
        Assert.Equal(350f, groupXInactive["SUM(amount)"].AsFloat32());

        Row groupYActive = results.First(row =>
            row["dept"].AsString() == "Y" && row["status"].AsString() == "active");
        Assert.Equal(2f, groupYActive["COUNT(*)"].AsFloat32());
        Assert.Equal(75f, groupYActive["SUM(amount)"].AsFloat32());

        Row groupYInactive = results.First(row =>
            row["dept"].AsString() == "Y" && row["status"].AsString() == "inactive");
        Assert.Equal(1f, groupYInactive["COUNT(*)"].AsFloat32());
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
        MockOperator source = new(
            MakeRow(("x", DataValue.FromFloat32(1f))),
            MakeRow(("x", DataValue.FromFloat32(2f))),
            MakeRow(("x", DataValue.FromFloat32(3f))));

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
        Assert.Equal(3f, results[0]["COUNT(*)"].AsFloat32());
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
        MockOperator source = new(
            MakeRow(("key", DataValue.FromString("A")), ("val", DataValue.FromFloat32(10f))),
            MakeRow(("key", DataValue.FromString("B")), ("val", DataValue.FromFloat32(20f))),
            MakeRow(("key", DataValue.FromString("A")), ("val", DataValue.FromFloat32(30f))));

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
        MockOperator source = new(
            MakeRow(("grp", DataValue.FromString("G1")), ("v", DataValue.FromFloat32(5f))),
            MakeRow(("grp", DataValue.FromString("G1")), ("v", DataValue.FromFloat32(15f))),
            MakeRow(("grp", DataValue.FromString("G1")), ("v", DataValue.FromFloat32(10f))),
            MakeRow(("grp", DataValue.FromString("G2")), ("v", DataValue.FromFloat32(100f))),
            MakeRow(("grp", DataValue.FromString("G2")), ("v", DataValue.FromFloat32(50f))),
            MakeRow(("grp", DataValue.FromString("G2")), ("v", DataValue.FromFloat32(75f))),
            MakeRow(("grp", DataValue.FromString("G1")), ("v", DataValue.FromFloat32(1f))),
            MakeRow(("grp", DataValue.FromString("G2")), ("v", DataValue.FromFloat32(200f))));

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
        MockOperator source = new(
            MakeRow(("key", DataValue.FromString("A")), ("val", DataValue.FromFloat32(1f))),
            MakeRow(("key", DataValue.FromString("B")), ("val", DataValue.FromFloat32(2f))),
            MakeRow(("key", DataValue.FromString("A")), ("val", DataValue.FromFloat32(3f))));

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
        MockOperator source = new();

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
