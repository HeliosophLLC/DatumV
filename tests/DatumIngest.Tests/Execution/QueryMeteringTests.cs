using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Integration tests verifying that <see cref="ExpressionEvaluator"/> accumulates
/// Query Unit costs on the attached <see cref="QueryMeter"/> when evaluating
/// scalar function calls.
/// </summary>
public class QueryMeteringTests : ServiceTestBase
{
    private static readonly string[] XColumns = ["x"];
    private ExpressionEvaluator _evaluator = new(FunctionRegistry.CreateDefault(), store: new Arena(), meter: new QueryMeter());

    /// <summary>
    /// Evaluating a single scalar function call adds its QU cost to the meter.
    /// </summary>
    [Fact]
    public async Task EvaluateFunction_AccumulatesQueryUnits()
    {
        Row row = MakeRow(["x"], [DataValue.FromString("hello")]);

        // `len` has a QU cost of 1 (default).
        await _evaluator.EvaluateAsync(
            new FunctionCallExpression("len", [new ColumnReference("x")]),
            row);

        Assert.Equal(1, _evaluator.QueryMeter!.QueryUnits);
    }

    /// <summary>
    /// Multiple function calls accumulate their costs additively.
    /// </summary>
    [Fact]
    public async Task MultipleFunctionCalls_AccumulateCosts()
    {
        Row row = MakeRow(["x"], [DataValue.FromFloat32(1.5f)]);

        // Three calls to `abs` (QU cost 1 each) → total 3 QU.
        for (int i = 0; i < 3; i++)
        {
            await _evaluator.EvaluateAsync(
                new FunctionCallExpression("abs", [new ColumnReference("x")]),
                row);
        }

        Assert.Equal(3, _evaluator.QueryMeter!.QueryUnits);
    }

    /// <summary>
    /// When no meter is attached, function evaluation works without error.
    /// </summary>
    [Fact]
    public async Task NoMeter_FunctionStillExecutes()
    {
        Row row = MakeRow(["x"], [DataValue.FromFloat32(4f)]);

        DataValue result = await _evaluator.EvaluateAsync(
            new FunctionCallExpression("sqrt", [new ColumnReference("x")]),
            row);

        Assert.Equal(2f, result.AsFloat32(), 0.001f);
    }

    /// <summary>
    /// Budget enforcement: adding costs that exceed the budget sets IsBudgetExceeded.
    /// </summary>
    [Fact]
    public async Task BudgetExceeded_AfterFunctionCalls()
    {
        using Arena arena = new();
        QueryMeter meter = new(budget: 2);
        ExpressionEvaluator evaluator = new(FunctionRegistry.CreateDefault(), meter, store: arena);
        Row row = MakeRow(["x"], [DataValue.FromFloat32(-5f)]);

        // Three calls to `abs` (QU cost 1 each) → 3 QU, budget is 2.
        for (int i = 0; i < 3; i++)
        {
            await evaluator.EvaluateAsync(
                new FunctionCallExpression("abs", [new ColumnReference("x")]),
                row);
        }

        Assert.True(meter.IsBudgetExceeded);
    }

    /// <summary>
    /// CAST expressions charge the same QU cost as a direct function call,
    /// because <see cref="CastFunction"/> is an <see cref="IScalarFunction"/> and
    /// the evaluator's <c>EvaluateCast</c> path must meter it identically.
    /// </summary>
    [Fact]
    public async Task EvaluateCast_AccumulatesQueryUnits()
    {
        Row row = MakeRow(["x"], [DataValue.FromFloat32(3.7f)]);

        // CAST(x AS String) — "cast" is a Tier 1 function (QU 1).
        await _evaluator.EvaluateAsync(
            new CastExpression(new ColumnReference("x"), "String"),
            row);

        Assert.Equal(1, _evaluator.QueryMeter!.QueryUnits);
    }

    /// <summary>
    /// Non-function expressions (literals, column references) do not add QU.
    /// </summary>
    [Fact]
    public async Task NonFunctionExpression_DoesNotAccumulate()
    {
        Row row = MakeRow(["x"], [DataValue.FromFloat32(42f)]);

        await _evaluator.EvaluateAsync(new LiteralExpression(1), row);
        await _evaluator.EvaluateAsync(new ColumnReference("x"), row);

        Assert.Equal(0, _evaluator.QueryMeter!.QueryUnits);
    }

    /// <summary>
    /// GroupByOperator accumulates QU for each aggregate Accumulate() call.
    /// SUM has a cost of 1 QU; 3 rows × 1 aggregate = 3 QU.
    /// </summary>
    [Fact]
    public async Task GroupByOperator_AggregateCalls_AccumulateQueryUnits()
    {
        QueryMeter meter = new();
        ExecutionContext context = CreateExecutionContext(meter: meter);

        MockOperator source = CreateMockOperator(XColumns,
            [1f],
            [2f],
            [3f]);

        // SELECT SUM(x) — global aggregation (no GROUP BY).
        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    FunctionRegistry.CreateDefault().TryGetAggregate("SUM")!,
                    [new ColumnReference("x")],
                    OutputName: "sum_x",
                    IsCountStar: false,
                    Distinct: false,
                    OrderBy: null)
            ]);

        await foreach (RowBatch batch in groupBy.ExecuteAsync(context)) { _ = batch.Count; }

        // 3 rows × SUM (QU 1 each) = 3 QU.
        Assert.Equal(3, meter.QueryUnits);
    }

    /// <summary>
    /// Heavy aggregate MEDIAN (QU 2) accumulates 2 QU per row.
    /// 4 rows × 2 QU = 8 QU.
    /// </summary>
    [Fact]
    public async Task GroupByOperator_HeavyAggregate_AccumulatesHigherCost()
    {
        QueryMeter meter = new();
        ExecutionContext context = CreateExecutionContext(meter: meter);

        MockOperator source = CreateMockOperator(XColumns,
            [4f],
            [1f],
            [3f],
            [2f]);

        // SELECT MEDIAN(x) — QU cost 2 per accumulation.
        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    FunctionRegistry.CreateDefault().TryGetAggregate("MEDIAN")!,
                    [new ColumnReference("x")],
                    OutputName: "median_x",
                    IsCountStar: false,
                    Distinct: false,
                    OrderBy: null)
            ]);

        await foreach (RowBatch batch in groupBy.ExecuteAsync(context)) { _ = batch.Count; }

        // 4 rows × MEDIAN (QU 2 each) = 8 QU.
        Assert.Equal(8, meter.QueryUnits);
    }

    /// <summary>
    /// WindowOperator accumulates QU for each window function computation.
    /// ROW_NUMBER has QU cost 1; 3 rows in the partition = 3 QU.
    /// </summary>
    [Fact]
    public async Task WindowOperator_WindowFunctionCalls_AccumulateQueryUnits()
    {
        QueryMeter meter = new();
        ExecutionContext context = CreateExecutionContext(meter: meter);

        MockOperator source = CreateMockOperator(XColumns,
            [1f],
            [2f],
            [3f]);

        IWindowFunction rowNumber = FunctionRegistry.CreateDefault().TryGetWindow("ROW_NUMBER")!;
        WindowOperator window = new(
            source,
            [
                new WindowColumn(
                    rowNumber,
                    ArgumentExpressions: [],
                    WindowSpecification: new WindowSpecification(
                        PartitionBy: null,
                        OrderBy: null,
                        Frame: null),
                    OutputName: "rn",
                    NullHandling: NullHandling.RespectNulls,
                    FromLast: false)
            ]);

        await foreach (RowBatch batch in window.ExecuteAsync(context)) { _ = batch.Count; }

        // 3 rows in partition × ROW_NUMBER (QU 1 per row) = 3 QU.
        Assert.Equal(3, meter.QueryUnits);
    }
}
