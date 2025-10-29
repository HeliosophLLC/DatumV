using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions.Aggregates;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for <see cref="GroupByOperator"/> covering single-key grouping,
/// composite-key grouping, global aggregation, and various aggregate functions.
/// </summary>
public class GroupByOperatorTests : ServiceTestBase
{
    private static readonly string[] XColumns = ["x"];
    private static readonly string[] PriceColumns = ["price"];
    private static readonly string[] CategoryValueColumns = ["category", "value"];
    private static readonly string[] DeptStatusAmountColumns = ["dept", "status", "amount"];
    private static readonly string[] GroupValColumns = ["group", "val"];
    private static readonly string[] CategoryPriceColumns = ["category", "price"];

    private async Task<List<Row>> CollectAsync(IQueryOperator op, ExecutionContext? context = null)
    {
        context ??= CreateExecutionContext();
        return await op.CollectRowsAsync(context);
    }

    // ─────────────── Global aggregation (no GROUP BY) ───────────────

    [Fact]
    public async Task GlobalAggregation_CountStar()
    {
        MockOperator source = CreateMockOperator(XColumns,
            [1f],
            [2f],
            [3f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(3L, results[0]["COUNT(*)"].AsInt64());
    }

    [Fact]
    public async Task GlobalAggregation_SumAndAvg()
    {
        MockOperator source = CreateMockOperator(PriceColumns,
            [10f],
            [20f],
            [30f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new SumFunction(),
                    [new ColumnReference("price")],
                    "SUM(price)"),
                new AggregateColumn(
                    new AvgFunction(),
                    [new ColumnReference("price")],
                    "AVG(price)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(60f, results[0]["SUM(price)"].AsFloat32());
        Assert.Equal(20.0, results[0]["AVG(price)"].AsFloat64());
    }

    // ─────────────── Single-key GROUP BY ───────────────

    [Fact]
    public async Task SingleKey_GroupBy_WithCount()
    {
        MockOperator source = CreateMockOperator(CategoryValueColumns,
            ["A", 1f],
            ["B", 2f],
            ["A", 3f],
            ["B", 4f],
            ["A", 5f]);

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

        List<Row> results = await CollectAsync(groupBy);

        Assert.Equal(2, results.Count);

        // Find group A and group B (order is not guaranteed).
        Row groupA = results.First(row => row["category"].AsString() == "A");
        Row groupB = results.First(row => row["category"].AsString() == "B");

        Assert.Equal(3L, groupA["COUNT(*)"].AsInt64());
        Assert.Equal(9f, groupA["SUM(value)"].AsFloat32());

        Assert.Equal(2L, groupB["COUNT(*)"].AsInt64());
        Assert.Equal(6f, groupB["SUM(value)"].AsFloat32());
    }

    // ─────────────── Composite-key GROUP BY ───────────────

    [Fact]
    public async Task CompositeKey_GroupBy()
    {
        MockOperator source = CreateMockOperator(DeptStatusAmountColumns,
            ["X", "active", 100f],
            ["X", "inactive", 200f],
            ["X", "active", 300f],
            ["Y", "active", 50f]);

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

        List<Row> results = await CollectAsync(groupBy);

        Assert.Equal(3, results.Count);

        Row groupXActive = results.First(row =>
            row["dept"].AsString() == "X" && row["status"].AsString() == "active");
        Assert.Equal(2L, groupXActive["COUNT(*)"].AsInt64());
        Assert.Equal(400f, groupXActive["SUM(amount)"].AsFloat32());

        Row groupXInactive = results.First(row =>
            row["dept"].AsString() == "X" && row["status"].AsString() == "inactive");
        Assert.Equal(1L, groupXInactive["COUNT(*)"].AsInt64());
        Assert.Equal(200f, groupXInactive["SUM(amount)"].AsFloat32());
    }

    // ─────────────── MIN / MAX ───────────────

    [Fact]
    public async Task GroupBy_MinMax()
    {
        MockOperator source = CreateMockOperator(GroupValColumns,
            ["G1", 5f],
            ["G1", 15f],
            ["G1", 10f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("group")],
            aggregateColumns:
            [
                new AggregateColumn(
                    new MinFunction(),
                    [new ColumnReference("val")],
                    "MIN(val)"),
                new AggregateColumn(
                    new MaxFunction(),
                    [new ColumnReference("val")],
                    "MAX(val)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(5f, results[0]["MIN(val)"].AsFloat32());
        Assert.Equal(15f, results[0]["MAX(val)"].AsFloat32());
    }

    // ─────────────── Empty input ───────────────

    [Fact]
    public async Task GroupBy_EmptyInput_WithGroupBy_ReturnsNoRows()
    {
        MockOperator source = CreateMockOperator(XColumns);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("x")],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GlobalAggregation_EmptyInput_ReturnsOneRow()
    {
        MockOperator source = CreateMockOperator(XColumns);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        // Global aggregation on empty input returns a single row with COUNT(*) = 0.
        Assert.Single(results);
        Assert.Equal(0L, results[0]["COUNT(*)"].AsInt64());
    }

    // ─────────────── Null handling ───────────────

    [Fact]
    public async Task GroupBy_NullGroupKeyCreatesGroup()
    {
        MockOperator source = CreateMockOperator(CategoryValueColumns,
            [DataValue.Null(DataKind.String), 1f],
            ["A", 2f],
            [DataValue.Null(DataKind.String), 3f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("category")],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Equal(2, results.Count);

        Row nullGroup = results.First(row => row["category"].IsNull);
        Assert.Equal(2L, nullGroup["COUNT(*)"].AsInt64());
    }

    // ─────────────── Output column schema ───────────────

    [Fact]
    public async Task OutputRow_HasCorrectColumnNames()
    {
        MockOperator source = CreateMockOperator(CategoryPriceColumns,
            ["A", 10f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("category")],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
                new AggregateColumn(
                    new SumFunction(),
                    [new ColumnReference("price")],
                    "SUM(price)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Row row = results[0];

        // Column access by name should work for all output columns.
        Assert.False(row["category"].IsNull);
        Assert.False(row["COUNT(*)"].IsNull);
        Assert.False(row["SUM(price)"].IsNull);
    }

    // ─────────────── Governor enforcement during materialization ───────────────

    /// <summary>
    /// When the Query Unit budget is exceeded during GROUP BY materialization,
    /// the operator throws <see cref="QueryBudgetExceededException"/> instead
    /// of silently consuming the entire input.
    /// </summary>
    [Fact]
    public async Task GroupBy_BudgetExceeded_ThrowsDuringMaterialization()
    {
        // Four rows, each evaluating abs() (QU cost 1) as the GROUP BY key.
        // Budget of 1: after the first row, consumed = 1 (not exceeded because
        // IsBudgetExceeded uses >). After the second row, consumed = 2 > 1,
        // so the third row's pre-evaluation check triggers the exception.
        MockOperator source = CreateMockOperator(XColumns,
            [1f],
            [2f],
            [3f],
            [4f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new FunctionCallExpression("abs", [new ColumnReference("x")])],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
            ]);

        QueryMeter meter = new(budget: 1);
        ExecutionContext context = CreateExecutionContext(meter: meter);

        await Assert.ThrowsAsync<QueryBudgetExceededException>(
            () => CollectAsync(groupBy, context));
    }

    /// <summary>
    /// When the cancellation token is cancelled, the GROUP BY operator
    /// throws <see cref="OperationCanceledException"/> during materialization
    /// instead of consuming the entire input.
    /// </summary>
    [Fact]
    public async Task GroupBy_CancellationToken_ThrowsDuringMaterialization()
    {
        MockOperator source = CreateMockOperator(XColumns,
            [1f],
            [2f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("x")],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
            ]);

        CancellationTokenSource cancellationTokenSource = new();
        cancellationTokenSource.Cancel();

        ExecutionContext context = CreateExecutionContext();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => CollectAsync(groupBy, context));
    }
}
