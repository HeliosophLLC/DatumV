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
/// Tests for <see cref="GroupByOperator"/> covering single-key grouping,
/// composite-key grouping, global aggregation, and various aggregate functions.
/// </summary>
public class GroupByOperatorTests
{
    private static ExecutionContext CreateContext()
    {
        return new ExecutionContext(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            new TableCatalog());
    }

    private static Row MakeRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = columns.Select(c => c.Name).ToArray();
        DataValue[] values = columns.Select(c => c.Value).ToArray();
        return new Row(names, values);
    }

    private static async Task<List<Row>> CollectAsync(IQueryOperator op, ExecutionContext? context = null)
    {
        context ??= CreateContext();
        List<Row> rows = [];
        await foreach (Row row in op.ExecuteAsync(context))
        {
            rows.Add(row);
        }

        return rows;
    }

    // ─────────────── Global aggregation (no GROUP BY) ───────────────

    [Fact]
    public async Task GlobalAggregation_CountStar()
    {
        MockOperator source = new(
            MakeRow(("x", DataValue.FromScalar(1f))),
            MakeRow(("x", DataValue.FromScalar(2f))),
            MakeRow(("x", DataValue.FromScalar(3f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(3f, results[0]["COUNT(*)"].AsScalar());
    }

    [Fact]
    public async Task GlobalAggregation_SumAndAvg()
    {
        MockOperator source = new(
            MakeRow(("price", DataValue.FromScalar(10f))),
            MakeRow(("price", DataValue.FromScalar(20f))),
            MakeRow(("price", DataValue.FromScalar(30f))));

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
        Assert.Equal(60f, results[0]["SUM(price)"].AsScalar());
        Assert.Equal(20f, results[0]["AVG(price)"].AsScalar());
    }

    // ─────────────── Single-key GROUP BY ───────────────

    [Fact]
    public async Task SingleKey_GroupBy_WithCount()
    {
        MockOperator source = new(
            MakeRow(("category", DataValue.FromString("A")), ("value", DataValue.FromScalar(1f))),
            MakeRow(("category", DataValue.FromString("B")), ("value", DataValue.FromScalar(2f))),
            MakeRow(("category", DataValue.FromString("A")), ("value", DataValue.FromScalar(3f))),
            MakeRow(("category", DataValue.FromString("B")), ("value", DataValue.FromScalar(4f))),
            MakeRow(("category", DataValue.FromString("A")), ("value", DataValue.FromScalar(5f))));

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

        Assert.Equal(3f, groupA["COUNT(*)"].AsScalar());
        Assert.Equal(9f, groupA["SUM(value)"].AsScalar());

        Assert.Equal(2f, groupB["COUNT(*)"].AsScalar());
        Assert.Equal(6f, groupB["SUM(value)"].AsScalar());
    }

    // ─────────────── Composite-key GROUP BY ───────────────

    [Fact]
    public async Task CompositeKey_GroupBy()
    {
        MockOperator source = new(
            MakeRow(("dept", DataValue.FromString("X")), ("status", DataValue.FromString("active")), ("amount", DataValue.FromScalar(100f))),
            MakeRow(("dept", DataValue.FromString("X")), ("status", DataValue.FromString("inactive")), ("amount", DataValue.FromScalar(200f))),
            MakeRow(("dept", DataValue.FromString("X")), ("status", DataValue.FromString("active")), ("amount", DataValue.FromScalar(300f))),
            MakeRow(("dept", DataValue.FromString("Y")), ("status", DataValue.FromString("active")), ("amount", DataValue.FromScalar(50f))));

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
        Assert.Equal(2f, groupXActive["COUNT(*)"].AsScalar());
        Assert.Equal(400f, groupXActive["SUM(amount)"].AsScalar());

        Row groupXInactive = results.First(row =>
            row["dept"].AsString() == "X" && row["status"].AsString() == "inactive");
        Assert.Equal(1f, groupXInactive["COUNT(*)"].AsScalar());
        Assert.Equal(200f, groupXInactive["SUM(amount)"].AsScalar());
    }

    // ─────────────── MIN / MAX ───────────────

    [Fact]
    public async Task GroupBy_MinMax()
    {
        MockOperator source = new(
            MakeRow(("group", DataValue.FromString("G1")), ("val", DataValue.FromScalar(5f))),
            MakeRow(("group", DataValue.FromString("G1")), ("val", DataValue.FromScalar(15f))),
            MakeRow(("group", DataValue.FromString("G1")), ("val", DataValue.FromScalar(10f))));

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
        Assert.Equal(5f, results[0]["MIN(val)"].AsScalar());
        Assert.Equal(15f, results[0]["MAX(val)"].AsScalar());
    }

    // ─────────────── Empty input ───────────────

    [Fact]
    public async Task GroupBy_EmptyInput_WithGroupBy_ReturnsNoRows()
    {
        MockOperator source = new();

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
        MockOperator source = new();

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
        Assert.Equal(0f, results[0]["COUNT(*)"].AsScalar());
    }

    // ─────────────── Null handling ───────────────

    [Fact]
    public async Task GroupBy_NullGroupKeyCreatesGroup()
    {
        MockOperator source = new(
            MakeRow(("category", DataValue.Null(DataKind.String)), ("value", DataValue.FromScalar(1f))),
            MakeRow(("category", DataValue.FromString("A")), ("value", DataValue.FromScalar(2f))),
            MakeRow(("category", DataValue.Null(DataKind.String)), ("value", DataValue.FromScalar(3f))));

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
        Assert.Equal(2f, nullGroup["COUNT(*)"].AsScalar());
    }

    // ─────────────── Output column schema ───────────────

    [Fact]
    public async Task OutputRow_HasCorrectColumnNames()
    {
        MockOperator source = new(
            MakeRow(("category", DataValue.FromString("A")), ("price", DataValue.FromScalar(10f))));

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
        Assert.NotNull(row["category"]);
        Assert.NotNull(row["COUNT(*)"]);
        Assert.NotNull(row["SUM(price)"]);
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
        MockOperator source = new(
            MakeRow(("x", DataValue.FromScalar(1f))),
            MakeRow(("x", DataValue.FromScalar(2f))),
            MakeRow(("x", DataValue.FromScalar(3f))),
            MakeRow(("x", DataValue.FromScalar(4f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new FunctionCallExpression("abs", [new ColumnReference("x")])],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
            ]);

        QueryMeter meter = new(budget: 1);
        ExecutionContext context = new(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            new TableCatalog(),
            meter);

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
        MockOperator source = new(
            MakeRow(("x", DataValue.FromScalar(1f))),
            MakeRow(("x", DataValue.FromScalar(2f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("x")],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
            ]);

        CancellationTokenSource cancellationTokenSource = new();
        cancellationTokenSource.Cancel();

        ExecutionContext context = new(
            cancellationTokenSource.Token,
            FunctionRegistry.CreateDefault(),
            new TableCatalog());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => CollectAsync(groupBy, context));
    }
}
