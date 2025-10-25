using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Functions.Aggregates;
using DatumIngest.Functions.Window;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for <see cref="WindowOperator"/> covering partitioning, ordering,
/// and end-to-end computation of window functions.
/// </summary>
public class WindowOperatorTests
{

    private static Row MakeRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = columns.Select(c => c.Name).ToArray();
        DataValue[] values = columns.Select(c => c.Value).ToArray();
        return new Row(names, values);
    }

    private static async Task<List<Row>> CollectAsync(IQueryOperator op, ExecutionContext? context = null)
    {
        context ??= TestExecutionContext.Create();
        return await op.CollectRowsAsync(context);
    }

    // ─────────────── Basic row numbering ───────────────

    [Fact]
    public async Task WindowOperator_RowNumber_NoPartition()
    {
        MockOperator source = new(
            MakeRow(("id", DataValue.FromFloat32(1f))),
            MakeRow(("id", DataValue.FromFloat32(2f))),
            MakeRow(("id", DataValue.FromFloat32(3f))));

        WindowSpecification spec = new(
            PartitionBy: null,
            OrderBy: [new OrderByItem(new ColumnReference("id"), SortDirection.Ascending)],
            Frame: null);

        WindowColumn column = new(
            new RowNumberFunction(),
            [],
            spec,
            "rn");

        WindowOperator window = new(source, [column]);
        List<Row> results = await CollectAsync(window);

        Assert.Equal(3, results.Count);
        // Each row should have the original "id" column plus "rn"
        Assert.Equal(2, results[0].FieldCount);
        Assert.Equal(1f, results[0]["rn"].AsFloat32());
        Assert.Equal(2f, results[1]["rn"].AsFloat32());
        Assert.Equal(3f, results[2]["rn"].AsFloat32());
    }

    // ─────────────── Partitioned row numbering ───────────────

    [Fact]
    public async Task WindowOperator_RowNumber_WithPartitionBy()
    {
        MockOperator source = new(
            MakeRow(("category", DataValue.FromString("A")), ("val", DataValue.FromFloat32(10f))),
            MakeRow(("category", DataValue.FromString("B")), ("val", DataValue.FromFloat32(20f))),
            MakeRow(("category", DataValue.FromString("A")), ("val", DataValue.FromFloat32(30f))),
            MakeRow(("category", DataValue.FromString("B")), ("val", DataValue.FromFloat32(40f))),
            MakeRow(("category", DataValue.FromString("A")), ("val", DataValue.FromFloat32(50f))));

        WindowSpecification spec = new(
            PartitionBy: [new ColumnReference("category")],
            OrderBy: [new OrderByItem(new ColumnReference("val"), SortDirection.Ascending)],
            Frame: null);

        WindowColumn column = new(
            new RowNumberFunction(),
            [],
            spec,
            "rn");

        WindowOperator window = new(source, [column]);
        List<Row> results = await CollectAsync(window);

        Assert.Equal(5, results.Count);

        // Verify row numbers are correct within each partition.
        // Rows are emitted in original order, so we need to verify per-partition numbering.
        List<float> categoryARowNumbers = results
            .Where(r => r["category"].AsString() == "A")
            .Select(r => r["rn"].AsFloat32())
            .OrderBy(n => n)
            .ToList();

        List<float> categoryBRowNumbers = results
            .Where(r => r["category"].AsString() == "B")
            .Select(r => r["rn"].AsFloat32())
            .OrderBy(n => n)
            .ToList();

        Assert.Equal([1f, 2f, 3f], categoryARowNumbers);
        Assert.Equal([1f, 2f], categoryBRowNumbers);
    }

    // ─────────────── Rank with ties ───────────────

    [Fact]
    public async Task WindowOperator_Rank_WithTies()
    {
        MockOperator source = new(
            MakeRow(("score", DataValue.FromFloat32(100f))),
            MakeRow(("score", DataValue.FromFloat32(90f))),
            MakeRow(("score", DataValue.FromFloat32(100f))),
            MakeRow(("score", DataValue.FromFloat32(80f))));

        WindowSpecification spec = new(
            PartitionBy: null,
            OrderBy: [new OrderByItem(new ColumnReference("score"), SortDirection.Descending)],
            Frame: null);

        WindowColumn column = new(
            new RankFunction(),
            [],
            spec,
            "rnk");

        WindowOperator window = new(source, [column]);
        List<Row> results = await CollectAsync(window);

        // After sorting desc: 100, 100, 90, 80 → ranks 1, 1, 3, 4
        // But results are in original order, so we check by score.
        List<(float Score, float Rank)> scored = results
            .Select(r => (r["score"].AsFloat32(), r["rnk"].AsFloat32()))
            .ToList();

        List<float> hundredRanks = scored.Where(s => s.Score == 100f).Select(s => s.Rank).ToList();
        List<float> ninetyRanks = scored.Where(s => s.Score == 90f).Select(s => s.Rank).ToList();
        List<float> eightyRanks = scored.Where(s => s.Score == 80f).Select(s => s.Rank).ToList();

        Assert.All(hundredRanks, r => Assert.Equal(1f, r));
        Assert.All(ninetyRanks, r => Assert.Equal(3f, r));
        Assert.All(eightyRanks, r => Assert.Equal(4f, r));
    }

    // ─────────────── Running total frame ───────────────

    [Fact]
    public async Task WindowOperator_Sum_RunningTotal()
    {
        MockOperator source = new(
            MakeRow(("val", DataValue.FromFloat32(10f))),
            MakeRow(("val", DataValue.FromFloat32(20f))),
            MakeRow(("val", DataValue.FromFloat32(30f))));

        WindowSpecification spec = new(
            PartitionBy: null,
            OrderBy: [new OrderByItem(new ColumnReference("val"), SortDirection.Ascending)],
            Frame: new WindowFrame(
                WindowFrameType.Rows,
                new UnboundedPrecedingBound(),
                new CurrentRowBound()));

        WindowColumn column = new(
            new AggregateWindowAdapter(new SumFunction()),
            [new ColumnReference("val")],
            spec,
            "running_total");

        WindowOperator window = new(source, [column]);
        List<Row> results = await CollectAsync(window);

        Assert.Equal(3, results.Count);

        // Values sorted: 10, 20, 30. Running totals: 10, 30, 60.
        // Results in original order (which happens to be the same order).
        Assert.Equal(10f, results[0]["running_total"].AsFloat32());
        Assert.Equal(30f, results[1]["running_total"].AsFloat32());
        Assert.Equal(60f, results[2]["running_total"].AsFloat32());
    }

    // ─────────────── Empty source ───────────────

    [Fact]
    public async Task WindowOperator_EmptySource_YieldsNoRows()
    {
        MockOperator source = new();

        WindowSpecification spec = new(null, null, null);
        WindowColumn column = new(
            new RowNumberFunction(),
            [],
            spec,
            "rn");

        WindowOperator window = new(source, [column]);
        List<Row> results = await CollectAsync(window);

        Assert.Empty(results);
    }

    // ─────────────── Multiple window columns with same spec ───────────────

    [Fact]
    public async Task WindowOperator_MultipleColumns_SameSpec()
    {
        MockOperator source = new(
            MakeRow(("val", DataValue.FromFloat32(10f))),
            MakeRow(("val", DataValue.FromFloat32(20f))),
            MakeRow(("val", DataValue.FromFloat32(30f))));

        WindowSpecification spec = new(
            PartitionBy: null,
            OrderBy: [new OrderByItem(new ColumnReference("val"), SortDirection.Ascending)],
            Frame: null);

        WindowColumn rowNumberColumn = new(
            new RowNumberFunction(),
            [],
            spec,
            "rn");

        WindowColumn rankColumn = new(
            new RankFunction(),
            [],
            spec,
            "rnk");

        WindowOperator window = new(source, [rowNumberColumn, rankColumn]);
        List<Row> results = await CollectAsync(window);

        Assert.Equal(3, results.Count);
        // 3 original fields + 2 window columns = ... wait, only 1 original field
        Assert.Equal(3, results[0].FieldCount); // val, rn, rnk
    }

    // ─────────────── LAG / LEAD through operator ───────────────

    [Fact]
    public async Task WindowOperator_Lag_ProducesPreviousValues()
    {
        MockOperator source = new(
            MakeRow(("val", DataValue.FromFloat32(10f))),
            MakeRow(("val", DataValue.FromFloat32(20f))),
            MakeRow(("val", DataValue.FromFloat32(30f))));

        WindowSpecification spec = new(
            PartitionBy: null,
            OrderBy: [new OrderByItem(new ColumnReference("val"), SortDirection.Ascending)],
            Frame: null);

        WindowColumn column = new(
            new LagFunction(),
            [new ColumnReference("val")],
            spec,
            "prev_val");

        WindowOperator window = new(source, [column]);
        List<Row> results = await CollectAsync(window);

        Assert.True(results[0]["prev_val"].IsNull);
        Assert.Equal(10f, results[1]["prev_val"].AsFloat32());
        Assert.Equal(20f, results[2]["prev_val"].AsFloat32());
    }

    // ─────────────── Governor enforcement during materialization ───────────────

    /// <summary>
    /// When the Query Unit budget is already exceeded, the window operator
    /// throws <see cref="QueryBudgetExceededException"/> during materialization
    /// instead of consuming the entire input.
    /// </summary>
    [Fact]
    public async Task WindowOperator_BudgetExceeded_ThrowsDuringMaterialization()
    {
        MockOperator source = new(
            MakeRow(("val", DataValue.FromFloat32(1f))),
            MakeRow(("val", DataValue.FromFloat32(2f))));

        WindowSpecification spec = new(
            PartitionBy: null,
            OrderBy: [new OrderByItem(new ColumnReference("val"), SortDirection.Ascending)],
            Frame: null);

        WindowColumn column = new(
            new RowNumberFunction(),
            [],
            spec,
            "rn");

        WindowOperator window = new(source, [column]);

        // Pre-exceed the budget so the check fires on the first materialized row.
        QueryMeter meter = new(budget: 5);
        meter.Add(6);

        ExecutionContext context = new(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            TestTableCatalog.Create(), new LocalBufferPool(), meter);

        await Assert.ThrowsAsync<QueryBudgetExceededException>(
            () => CollectAsync(window, context));
    }

    /// <summary>
    /// When the cancellation token is cancelled, the window operator
    /// throws <see cref="OperationCanceledException"/> during materialization.
    /// </summary>
    [Fact]
    public async Task WindowOperator_CancellationToken_ThrowsDuringMaterialization()
    {
        MockOperator source = new(
            MakeRow(("val", DataValue.FromFloat32(1f))),
            MakeRow(("val", DataValue.FromFloat32(2f))));

        WindowSpecification spec = new(
            PartitionBy: null,
            OrderBy: [new OrderByItem(new ColumnReference("val"), SortDirection.Ascending)],
            Frame: null);

        WindowColumn column = new(
            new RowNumberFunction(),
            [],
            spec,
            "rn");

        WindowOperator window = new(source, [column]);

        CancellationTokenSource cancellationTokenSource = new();
        cancellationTokenSource.Cancel();

        ExecutionContext context = new(
            cancellationTokenSource.Token,
            FunctionRegistry.CreateDefault(),
            TestTableCatalog.Create(), new LocalBufferPool());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => CollectAsync(window, context));
    }
}
