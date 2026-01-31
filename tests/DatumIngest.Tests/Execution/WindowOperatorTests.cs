using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Functions.Aggregates;
using DatumIngest.Functions.Window;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for <see cref="WindowOperator"/> covering partitioning, ordering,
/// and end-to-end computation of window functions.
/// </summary>
public class WindowOperatorTests : ServiceTestBase
{
    private static readonly string[] IdColumns = ["id"];
    private static readonly string[] CategoryValColumns = ["category", "val"];
    private static readonly string[] ScoreColumns = ["score"];
    private static readonly string[] ValColumns = ["val"];

    private async Task<List<Row>> CollectAsync(IQueryOperator op, ExecutionContext? context = null)
    {
        context ??= CreateExecutionContext();
        return await op.CollectRowsAsync(context);
    }

    // ─────────────── Basic row numbering ───────────────

    [Fact]
    public async Task WindowOperator_RowNumber_NoPartition()
    {
        MockOperator source = CreateMockOperator(IdColumns,
            [1f],
            [2f],
            [3f]);

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
        MockOperator source = CreateMockOperator(CategoryValColumns,
            ["A", 10f],
            ["B", 20f],
            ["A", 30f],
            ["B", 40f],
            ["A", 50f]);

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
        MockOperator source = CreateMockOperator(ScoreColumns,
            [100f],
            [90f],
            [100f],
            [80f]);

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
        MockOperator source = CreateMockOperator(ValColumns,
            [10f],
            [20f],
            [30f]);

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
        MockOperator source = CreateMockOperator(ValColumns);

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
        MockOperator source = CreateMockOperator(ValColumns,
            [10f],
            [20f],
            [30f]);

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
        MockOperator source = CreateMockOperator(ValColumns,
            [10f],
            [20f],
            [30f]);

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
        MockOperator source = CreateMockOperator(ValColumns,
            [1f],
            [2f]);

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

        Pool pool = GetService<Pool>();
        ExecutionContext context = new(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            CreateCatalog(),
            pool,
            meter);

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
        MockOperator source = CreateMockOperator(ValColumns,
            [1f],
            [2f]);

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

        Pool pool = GetService<Pool>();
        ExecutionContext context = new(
            cancellationTokenSource.Token,
            FunctionRegistry.CreateDefault(),
            CreateCatalog(),
            pool);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => CollectAsync(window, context));
    }
}
