using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Aggregates;
using DatumIngest.Functions.Window;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Tests for the built-in window function implementations
/// (ROW_NUMBER, RANK, DENSE_RANK, NTILE, LAG, LEAD) and
/// the <see cref="AggregateWindowAdapter"/>.
/// </summary>
public class WindowFunctionTests
{
    private static ExpressionEvaluator CreateEvaluator()
    {
        return new ExpressionEvaluator(FunctionRegistry.CreateDefault());
    }

    private static Row MakeRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = columns.Select(c => c.Name).ToArray();
        DataValue[] values = columns.Select(c => c.Value).ToArray();
        return new Row(names, values);
    }

    /// <summary>
    /// Helper that runs a window computation over a partition of rows.
    /// </summary>
    private static DataValue[] ComputeWindow(
        IWindowFunction function,
        IReadOnlyList<Row> partitionRows,
        IReadOnlyList<Expression>? argumentExpressions = null,
        IReadOnlyList<OrderByItem>? orderByItems = null,
        WindowFrame? frame = null)
    {
        ExpressionEvaluator evaluator = CreateEvaluator();
        IWindowComputation computation = function.CreateComputation();
        DataValue[] results = new DataValue[partitionRows.Count];
        computation.Compute(
            partitionRows,
            argumentExpressions ?? [],
            evaluator,
            orderByItems,
            frame,
            results);
        return results;
    }

    // ─────────────── ROW_NUMBER ───────────────

    [Fact]
    public void RowNumber_SequentialNumbersStartingAtOne()
    {
        RowNumberFunction function = new();
        List<Row> rows =
        [
            MakeRow(("x", DataValue.FromScalar(10f))),
            MakeRow(("x", DataValue.FromScalar(20f))),
            MakeRow(("x", DataValue.FromScalar(30f))),
        ];

        DataValue[] results = ComputeWindow(function, rows);

        Assert.Equal(1f, results[0].AsScalar());
        Assert.Equal(2f, results[1].AsScalar());
        Assert.Equal(3f, results[2].AsScalar());
    }

    [Fact]
    public void RowNumber_SingleRow()
    {
        RowNumberFunction function = new();
        List<Row> rows = [MakeRow(("x", DataValue.FromScalar(1f)))];

        DataValue[] results = ComputeWindow(function, rows);

        Assert.Single(results);
        Assert.Equal(1f, results[0].AsScalar());
    }

    [Fact]
    public void RowNumber_ValidateArguments_RejectsArguments()
    {
        RowNumberFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Scalar]));
    }

    [Fact]
    public void RowNumber_ValidateArguments_AcceptsNoArguments()
    {
        RowNumberFunction function = new();
        DataKind result = function.ValidateArguments([]);
        Assert.Equal(DataKind.Scalar, result);
    }

    // ─────────────── RANK ───────────────

    [Fact]
    public void Rank_WithTies_ProducesGaps()
    {
        RankFunction function = new();
        List<Row> rows =
        [
            MakeRow(("score", DataValue.FromScalar(100f))),
            MakeRow(("score", DataValue.FromScalar(100f))),
            MakeRow(("score", DataValue.FromScalar(90f))),
            MakeRow(("score", DataValue.FromScalar(80f))),
        ];

        IReadOnlyList<OrderByItem> orderBy =
        [
            new OrderByItem(new ColumnReference("score"), SortDirection.Descending),
        ];

        DataValue[] results = ComputeWindow(function, rows, orderByItems: orderBy);

        // Tied at rank 1, then rank 3 (gap), then rank 4
        Assert.Equal(1f, results[0].AsScalar());
        Assert.Equal(1f, results[1].AsScalar());
        Assert.Equal(3f, results[2].AsScalar());
        Assert.Equal(4f, results[3].AsScalar());
    }

    [Fact]
    public void Rank_NoTies_SequentialRanks()
    {
        RankFunction function = new();
        List<Row> rows =
        [
            MakeRow(("score", DataValue.FromScalar(100f))),
            MakeRow(("score", DataValue.FromScalar(90f))),
            MakeRow(("score", DataValue.FromScalar(80f))),
        ];

        IReadOnlyList<OrderByItem> orderBy =
        [
            new OrderByItem(new ColumnReference("score"), SortDirection.Descending),
        ];

        DataValue[] results = ComputeWindow(function, rows, orderByItems: orderBy);

        Assert.Equal(1f, results[0].AsScalar());
        Assert.Equal(2f, results[1].AsScalar());
        Assert.Equal(3f, results[2].AsScalar());
    }

    // ─────────────── DENSE_RANK ───────────────

    [Fact]
    public void DenseRank_WithTies_NoGaps()
    {
        DenseRankFunction function = new();
        List<Row> rows =
        [
            MakeRow(("score", DataValue.FromScalar(100f))),
            MakeRow(("score", DataValue.FromScalar(100f))),
            MakeRow(("score", DataValue.FromScalar(90f))),
            MakeRow(("score", DataValue.FromScalar(80f))),
        ];

        IReadOnlyList<OrderByItem> orderBy =
        [
            new OrderByItem(new ColumnReference("score"), SortDirection.Descending),
        ];

        DataValue[] results = ComputeWindow(function, rows, orderByItems: orderBy);

        // Tied at rank 1, then rank 2 (no gap), then rank 3
        Assert.Equal(1f, results[0].AsScalar());
        Assert.Equal(1f, results[1].AsScalar());
        Assert.Equal(2f, results[2].AsScalar());
        Assert.Equal(3f, results[3].AsScalar());
    }

    // ─────────────── NTILE ───────────────

    [Fact]
    public void Ntile_EvenDistribution()
    {
        NtileFunction function = new();
        List<Row> rows =
        [
            MakeRow(("x", DataValue.FromScalar(1f))),
            MakeRow(("x", DataValue.FromScalar(2f))),
            MakeRow(("x", DataValue.FromScalar(3f))),
            MakeRow(("x", DataValue.FromScalar(4f))),
        ];

        // NTILE(2) on 4 rows → buckets 1,1,2,2
        IReadOnlyList<Expression> arguments = [new LiteralExpression(2)];
        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments);

        Assert.Equal(1f, results[0].AsScalar());
        Assert.Equal(1f, results[1].AsScalar());
        Assert.Equal(2f, results[2].AsScalar());
        Assert.Equal(2f, results[3].AsScalar());
    }

    [Fact]
    public void Ntile_UnevenDistribution()
    {
        NtileFunction function = new();
        List<Row> rows =
        [
            MakeRow(("x", DataValue.FromScalar(1f))),
            MakeRow(("x", DataValue.FromScalar(2f))),
            MakeRow(("x", DataValue.FromScalar(3f))),
            MakeRow(("x", DataValue.FromScalar(4f))),
            MakeRow(("x", DataValue.FromScalar(5f))),
        ];

        // NTILE(3) on 5 rows → 2+2+1 distribution → buckets 1,1,2,2,3
        IReadOnlyList<Expression> arguments = [new LiteralExpression(3)];
        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments);

        Assert.Equal(1f, results[0].AsScalar());
        Assert.Equal(1f, results[1].AsScalar());
        Assert.Equal(2f, results[2].AsScalar());
        Assert.Equal(2f, results[3].AsScalar());
        Assert.Equal(3f, results[4].AsScalar());
    }

    [Fact]
    public void Ntile_ValidateArguments_RequiresOneArgument()
    {
        NtileFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([]));
    }

    // ─────────────── LAG ───────────────

    [Fact]
    public void Lag_DefaultOffset_ReturnsPreviousRow()
    {
        LagFunction function = new();
        List<Row> rows =
        [
            MakeRow(("val", DataValue.FromScalar(10f))),
            MakeRow(("val", DataValue.FromScalar(20f))),
            MakeRow(("val", DataValue.FromScalar(30f))),
        ];

        IReadOnlyList<Expression> arguments = [new ColumnReference("val")];
        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments);

        Assert.True(results[0].IsNull);
        Assert.Equal(10f, results[1].AsScalar());
        Assert.Equal(20f, results[2].AsScalar());
    }

    [Fact]
    public void Lag_CustomOffset()
    {
        LagFunction function = new();
        List<Row> rows =
        [
            MakeRow(("val", DataValue.FromScalar(10f))),
            MakeRow(("val", DataValue.FromScalar(20f))),
            MakeRow(("val", DataValue.FromScalar(30f))),
            MakeRow(("val", DataValue.FromScalar(40f))),
        ];

        // LAG(val, 2) — offset of 2
        IReadOnlyList<Expression> arguments =
        [
            new ColumnReference("val"),
            new LiteralExpression(2),
        ];

        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments);

        Assert.True(results[0].IsNull);
        Assert.True(results[1].IsNull);
        Assert.Equal(10f, results[2].AsScalar());
        Assert.Equal(20f, results[3].AsScalar());
    }

    [Fact]
    public void Lag_CustomDefault()
    {
        LagFunction function = new();
        List<Row> rows =
        [
            MakeRow(("val", DataValue.FromScalar(10f))),
            MakeRow(("val", DataValue.FromScalar(20f))),
        ];

        // LAG(val, 1, -1) — offset 1, default -1
        IReadOnlyList<Expression> arguments =
        [
            new ColumnReference("val"),
            new LiteralExpression(1),
            new LiteralExpression(-1),
        ];

        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments);

        Assert.Equal(-1f, results[0].AsScalar());
        Assert.Equal(10f, results[1].AsScalar());
    }

    // ─────────────── LEAD ───────────────

    [Fact]
    public void Lead_DefaultOffset_ReturnsNextRow()
    {
        LeadFunction function = new();
        List<Row> rows =
        [
            MakeRow(("val", DataValue.FromScalar(10f))),
            MakeRow(("val", DataValue.FromScalar(20f))),
            MakeRow(("val", DataValue.FromScalar(30f))),
        ];

        IReadOnlyList<Expression> arguments = [new ColumnReference("val")];
        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments);

        Assert.Equal(20f, results[0].AsScalar());
        Assert.Equal(30f, results[1].AsScalar());
        Assert.True(results[2].IsNull);
    }

    [Fact]
    public void Lead_CustomOffset()
    {
        LeadFunction function = new();
        List<Row> rows =
        [
            MakeRow(("val", DataValue.FromScalar(10f))),
            MakeRow(("val", DataValue.FromScalar(20f))),
            MakeRow(("val", DataValue.FromScalar(30f))),
            MakeRow(("val", DataValue.FromScalar(40f))),
        ];

        // LEAD(val, 2) — offset of 2
        IReadOnlyList<Expression> arguments =
        [
            new ColumnReference("val"),
            new LiteralExpression(2),
        ];

        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments);

        Assert.Equal(30f, results[0].AsScalar());
        Assert.Equal(40f, results[1].AsScalar());
        Assert.True(results[2].IsNull);
        Assert.True(results[3].IsNull);
    }

    [Fact]
    public void Lead_CustomDefault()
    {
        LeadFunction function = new();
        List<Row> rows =
        [
            MakeRow(("val", DataValue.FromScalar(10f))),
            MakeRow(("val", DataValue.FromScalar(20f))),
        ];

        // LEAD(val, 1, 999) — offset 1, default 999
        IReadOnlyList<Expression> arguments =
        [
            new ColumnReference("val"),
            new LiteralExpression(1),
            new LiteralExpression(999),
        ];

        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments);

        Assert.Equal(20f, results[0].AsScalar());
        Assert.Equal(999f, results[1].AsScalar());
    }

    // ─────────────── AggregateWindowAdapter ───────────────

    [Fact]
    public void AggregateWindowAdapter_Sum_WholePartition()
    {
        AggregateWindowAdapter function = new(new SumFunction());
        List<Row> rows =
        [
            MakeRow(("val", DataValue.FromScalar(10f))),
            MakeRow(("val", DataValue.FromScalar(20f))),
            MakeRow(("val", DataValue.FromScalar(30f))),
        ];

        IReadOnlyList<Expression> arguments = [new ColumnReference("val")];

        // No frame → whole partition, sum = 60 for every row
        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments);

        Assert.Equal(60f, results[0].AsScalar());
        Assert.Equal(60f, results[1].AsScalar());
        Assert.Equal(60f, results[2].AsScalar());
    }

    [Fact]
    public void AggregateWindowAdapter_Sum_RunningTotal()
    {
        AggregateWindowAdapter function = new(new SumFunction());
        List<Row> rows =
        [
            MakeRow(("val", DataValue.FromScalar(10f))),
            MakeRow(("val", DataValue.FromScalar(20f))),
            MakeRow(("val", DataValue.FromScalar(30f))),
        ];

        IReadOnlyList<Expression> arguments = [new ColumnReference("val")];

        // Frame: ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW → running total
        WindowFrame frame = new(
            WindowFrameType.Rows,
            new UnboundedPrecedingBound(),
            new CurrentRowBound());

        DataValue[] results = ComputeWindow(function, rows,
            argumentExpressions: arguments, frame: frame);

        Assert.Equal(10f, results[0].AsScalar());
        Assert.Equal(30f, results[1].AsScalar());
        Assert.Equal(60f, results[2].AsScalar());
    }

    [Fact]
    public void AggregateWindowAdapter_Count_WholePartition()
    {
        AggregateWindowAdapter function = new(new CountFunction());
        List<Row> rows =
        [
            MakeRow(("val", DataValue.FromScalar(10f))),
            MakeRow(("val", DataValue.FromScalar(20f))),
            MakeRow(("val", DataValue.FromScalar(30f))),
        ];

        IReadOnlyList<Expression> arguments = [new ColumnReference("val")];
        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments);

        Assert.Equal(3f, results[0].AsScalar());
        Assert.Equal(3f, results[1].AsScalar());
        Assert.Equal(3f, results[2].AsScalar());
    }

    [Fact]
    public void AggregateWindowAdapter_Avg_SlidingWindow()
    {
        AggregateWindowAdapter function = new(new AvgFunction());
        List<Row> rows =
        [
            MakeRow(("val", DataValue.FromScalar(10f))),
            MakeRow(("val", DataValue.FromScalar(20f))),
            MakeRow(("val", DataValue.FromScalar(30f))),
            MakeRow(("val", DataValue.FromScalar(40f))),
        ];

        IReadOnlyList<Expression> arguments = [new ColumnReference("val")];

        // Frame: ROWS BETWEEN 1 PRECEDING AND 1 FOLLOWING → 3-row moving average
        WindowFrame frame = new(
            WindowFrameType.Rows,
            new PrecedingBound(1),
            new FollowingBound(1));

        DataValue[] results = ComputeWindow(function, rows,
            argumentExpressions: arguments, frame: frame);

        // Row 0: avg(10,20) = 15
        Assert.Equal(15f, results[0].AsScalar());
        // Row 1: avg(10,20,30) = 20
        Assert.Equal(20f, results[1].AsScalar());
        // Row 2: avg(20,30,40) = 30
        Assert.Equal(30f, results[2].AsScalar());
        // Row 3: avg(30,40) = 35
        Assert.Equal(35f, results[3].AsScalar());
    }

    // ─────────────── FunctionRegistry integration ───────────────

    [Fact]
    public void FunctionRegistry_RegistersAllWindowFunctions()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();

        Assert.NotNull(registry.TryGetWindow("ROW_NUMBER"));
        Assert.NotNull(registry.TryGetWindow("RANK"));
        Assert.NotNull(registry.TryGetWindow("DENSE_RANK"));
        Assert.NotNull(registry.TryGetWindow("NTILE"));
        Assert.NotNull(registry.TryGetWindow("LAG"));
        Assert.NotNull(registry.TryGetWindow("LEAD"));
    }

    [Fact]
    public void FunctionRegistry_TryGetWindowOrAggregate_FindsWindowFunction()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();

        IWindowFunction? function = registry.TryGetWindowOrAggregate("ROW_NUMBER");
        Assert.NotNull(function);
        Assert.Equal("ROW_NUMBER", function.Name);
    }

    [Fact]
    public void FunctionRegistry_TryGetWindowOrAggregate_WrapsAggregate()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();

        // SUM is registered as aggregate, not as a dedicated window function
        IWindowFunction? function = registry.TryGetWindowOrAggregate("SUM");
        Assert.NotNull(function);
        Assert.IsType<AggregateWindowAdapter>(function);
    }

    [Fact]
    public void FunctionRegistry_TryGetWindowOrAggregate_ReturnsFalseForUnknown()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();

        Assert.Null(registry.TryGetWindowOrAggregate("NONEXISTENT"));
    }

    [Fact]
    public void FunctionRegistry_WindowFunctionNames_IncludesAllSix()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();

        List<string> names = registry.WindowFunctionNames.ToList();
        Assert.Contains("ROW_NUMBER", names);
        Assert.Contains("RANK", names);
        Assert.Contains("DENSE_RANK", names);
        Assert.Contains("NTILE", names);
        Assert.Contains("LAG", names);
        Assert.Contains("LEAD", names);
    }
}
