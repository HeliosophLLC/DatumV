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
        WindowFrame? frame = null,
        NullHandling nullHandling = NullHandling.RespectNulls,
        bool fromLast = false)
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
            results,
            nullHandling,
            fromLast);
        return results;
    }

    // ─────────────── ROW_NUMBER ───────────────

    [Fact]
    public void RowNumber_SequentialNumbersStartingAtOne()
    {
        RowNumberFunction function = new();
        List<Row> rows =
        [
            MakeRow(("x", DataValue.FromFloat32(10f))),
            MakeRow(("x", DataValue.FromFloat32(20f))),
            MakeRow(("x", DataValue.FromFloat32(30f))),
        ];

        DataValue[] results = ComputeWindow(function, rows);

        Assert.Equal(1f, results[0].AsFloat32());
        Assert.Equal(2f, results[1].AsFloat32());
        Assert.Equal(3f, results[2].AsFloat32());
    }

    [Fact]
    public void RowNumber_SingleRow()
    {
        RowNumberFunction function = new();
        List<Row> rows = [MakeRow(("x", DataValue.FromFloat32(1f)))];

        DataValue[] results = ComputeWindow(function, rows);

        Assert.Single(results);
        Assert.Equal(1f, results[0].AsFloat32());
    }

    [Fact]
    public void RowNumber_ValidateArguments_RejectsArguments()
    {
        RowNumberFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Float32]));
    }

    [Fact]
    public void RowNumber_ValidateArguments_AcceptsNoArguments()
    {
        RowNumberFunction function = new();
        DataKind result = function.ValidateArguments([]);
        Assert.Equal(DataKind.Float32, result);
    }

    // ─────────────── RANK ───────────────

    [Fact]
    public void Rank_WithTies_ProducesGaps()
    {
        RankFunction function = new();
        List<Row> rows =
        [
            MakeRow(("score", DataValue.FromFloat32(100f))),
            MakeRow(("score", DataValue.FromFloat32(100f))),
            MakeRow(("score", DataValue.FromFloat32(90f))),
            MakeRow(("score", DataValue.FromFloat32(80f))),
        ];

        IReadOnlyList<OrderByItem> orderBy =
        [
            new OrderByItem(new ColumnReference("score"), SortDirection.Descending),
        ];

        DataValue[] results = ComputeWindow(function, rows, orderByItems: orderBy);

        // Tied at rank 1, then rank 3 (gap), then rank 4
        Assert.Equal(1f, results[0].AsFloat32());
        Assert.Equal(1f, results[1].AsFloat32());
        Assert.Equal(3f, results[2].AsFloat32());
        Assert.Equal(4f, results[3].AsFloat32());
    }

    [Fact]
    public void Rank_NoTies_SequentialRanks()
    {
        RankFunction function = new();
        List<Row> rows =
        [
            MakeRow(("score", DataValue.FromFloat32(100f))),
            MakeRow(("score", DataValue.FromFloat32(90f))),
            MakeRow(("score", DataValue.FromFloat32(80f))),
        ];

        IReadOnlyList<OrderByItem> orderBy =
        [
            new OrderByItem(new ColumnReference("score"), SortDirection.Descending),
        ];

        DataValue[] results = ComputeWindow(function, rows, orderByItems: orderBy);

        Assert.Equal(1f, results[0].AsFloat32());
        Assert.Equal(2f, results[1].AsFloat32());
        Assert.Equal(3f, results[2].AsFloat32());
    }

    // ─────────────── DENSE_RANK ───────────────

    [Fact]
    public void DenseRank_WithTies_NoGaps()
    {
        DenseRankFunction function = new();
        List<Row> rows =
        [
            MakeRow(("score", DataValue.FromFloat32(100f))),
            MakeRow(("score", DataValue.FromFloat32(100f))),
            MakeRow(("score", DataValue.FromFloat32(90f))),
            MakeRow(("score", DataValue.FromFloat32(80f))),
        ];

        IReadOnlyList<OrderByItem> orderBy =
        [
            new OrderByItem(new ColumnReference("score"), SortDirection.Descending),
        ];

        DataValue[] results = ComputeWindow(function, rows, orderByItems: orderBy);

        // Tied at rank 1, then rank 2 (no gap), then rank 3
        Assert.Equal(1f, results[0].AsFloat32());
        Assert.Equal(1f, results[1].AsFloat32());
        Assert.Equal(2f, results[2].AsFloat32());
        Assert.Equal(3f, results[3].AsFloat32());
    }

    // ─────────────── NTILE ───────────────

    [Fact]
    public void Ntile_EvenDistribution()
    {
        NtileFunction function = new();
        List<Row> rows =
        [
            MakeRow(("x", DataValue.FromFloat32(1f))),
            MakeRow(("x", DataValue.FromFloat32(2f))),
            MakeRow(("x", DataValue.FromFloat32(3f))),
            MakeRow(("x", DataValue.FromFloat32(4f))),
        ];

        // NTILE(2) on 4 rows → buckets 1,1,2,2
        IReadOnlyList<Expression> arguments = [new LiteralExpression(2)];
        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments);

        Assert.Equal(1f, results[0].AsFloat32());
        Assert.Equal(1f, results[1].AsFloat32());
        Assert.Equal(2f, results[2].AsFloat32());
        Assert.Equal(2f, results[3].AsFloat32());
    }

    [Fact]
    public void Ntile_UnevenDistribution()
    {
        NtileFunction function = new();
        List<Row> rows =
        [
            MakeRow(("x", DataValue.FromFloat32(1f))),
            MakeRow(("x", DataValue.FromFloat32(2f))),
            MakeRow(("x", DataValue.FromFloat32(3f))),
            MakeRow(("x", DataValue.FromFloat32(4f))),
            MakeRow(("x", DataValue.FromFloat32(5f))),
        ];

        // NTILE(3) on 5 rows → 2+2+1 distribution → buckets 1,1,2,2,3
        IReadOnlyList<Expression> arguments = [new LiteralExpression(3)];
        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments);

        Assert.Equal(1f, results[0].AsFloat32());
        Assert.Equal(1f, results[1].AsFloat32());
        Assert.Equal(2f, results[2].AsFloat32());
        Assert.Equal(2f, results[3].AsFloat32());
        Assert.Equal(3f, results[4].AsFloat32());
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
            MakeRow(("val", DataValue.FromFloat32(10f))),
            MakeRow(("val", DataValue.FromFloat32(20f))),
            MakeRow(("val", DataValue.FromFloat32(30f))),
        ];

        IReadOnlyList<Expression> arguments = [new ColumnReference("val")];
        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments);

        Assert.True(results[0].IsNull);
        Assert.Equal(10f, results[1].AsFloat32());
        Assert.Equal(20f, results[2].AsFloat32());
    }

    [Fact]
    public void Lag_CustomOffset()
    {
        LagFunction function = new();
        List<Row> rows =
        [
            MakeRow(("val", DataValue.FromFloat32(10f))),
            MakeRow(("val", DataValue.FromFloat32(20f))),
            MakeRow(("val", DataValue.FromFloat32(30f))),
            MakeRow(("val", DataValue.FromFloat32(40f))),
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
        Assert.Equal(10f, results[2].AsFloat32());
        Assert.Equal(20f, results[3].AsFloat32());
    }

    [Fact]
    public void Lag_CustomDefault()
    {
        LagFunction function = new();
        List<Row> rows =
        [
            MakeRow(("val", DataValue.FromFloat32(10f))),
            MakeRow(("val", DataValue.FromFloat32(20f))),
        ];

        // LAG(val, 1, -1) — offset 1, default -1
        IReadOnlyList<Expression> arguments =
        [
            new ColumnReference("val"),
            new LiteralExpression(1),
            new LiteralExpression(-1),
        ];

        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments);

        Assert.Equal(-1f, results[0].AsFloat32());
        Assert.Equal(10f, results[1].AsFloat32());
    }

    // ─────────────── LEAD ───────────────

    [Fact]
    public void Lead_DefaultOffset_ReturnsNextRow()
    {
        LeadFunction function = new();
        List<Row> rows =
        [
            MakeRow(("val", DataValue.FromFloat32(10f))),
            MakeRow(("val", DataValue.FromFloat32(20f))),
            MakeRow(("val", DataValue.FromFloat32(30f))),
        ];

        IReadOnlyList<Expression> arguments = [new ColumnReference("val")];
        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments);

        Assert.Equal(20f, results[0].AsFloat32());
        Assert.Equal(30f, results[1].AsFloat32());
        Assert.True(results[2].IsNull);
    }

    [Fact]
    public void Lead_CustomOffset()
    {
        LeadFunction function = new();
        List<Row> rows =
        [
            MakeRow(("val", DataValue.FromFloat32(10f))),
            MakeRow(("val", DataValue.FromFloat32(20f))),
            MakeRow(("val", DataValue.FromFloat32(30f))),
            MakeRow(("val", DataValue.FromFloat32(40f))),
        ];

        // LEAD(val, 2) — offset of 2
        IReadOnlyList<Expression> arguments =
        [
            new ColumnReference("val"),
            new LiteralExpression(2),
        ];

        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments);

        Assert.Equal(30f, results[0].AsFloat32());
        Assert.Equal(40f, results[1].AsFloat32());
        Assert.True(results[2].IsNull);
        Assert.True(results[3].IsNull);
    }

    [Fact]
    public void Lead_CustomDefault()
    {
        LeadFunction function = new();
        List<Row> rows =
        [
            MakeRow(("val", DataValue.FromFloat32(10f))),
            MakeRow(("val", DataValue.FromFloat32(20f))),
        ];

        // LEAD(val, 1, 999) — offset 1, default 999
        IReadOnlyList<Expression> arguments =
        [
            new ColumnReference("val"),
            new LiteralExpression(1),
            new LiteralExpression(999),
        ];

        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments);

        Assert.Equal(20f, results[0].AsFloat32());
        Assert.Equal(999f, results[1].AsFloat32());
    }

    // ─────────────── AggregateWindowAdapter ───────────────

    [Fact]
    public void AggregateWindowAdapter_Sum_WholePartition()
    {
        AggregateWindowAdapter function = new(new SumFunction());
        List<Row> rows =
        [
            MakeRow(("val", DataValue.FromFloat32(10f))),
            MakeRow(("val", DataValue.FromFloat32(20f))),
            MakeRow(("val", DataValue.FromFloat32(30f))),
        ];

        IReadOnlyList<Expression> arguments = [new ColumnReference("val")];

        // No frame → whole partition, sum = 60 for every row
        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments);

        Assert.Equal(60f, results[0].AsFloat32());
        Assert.Equal(60f, results[1].AsFloat32());
        Assert.Equal(60f, results[2].AsFloat32());
    }

    [Fact]
    public void AggregateWindowAdapter_Sum_RunningTotal()
    {
        AggregateWindowAdapter function = new(new SumFunction());
        List<Row> rows =
        [
            MakeRow(("val", DataValue.FromFloat32(10f))),
            MakeRow(("val", DataValue.FromFloat32(20f))),
            MakeRow(("val", DataValue.FromFloat32(30f))),
        ];

        IReadOnlyList<Expression> arguments = [new ColumnReference("val")];

        // Frame: ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW → running total
        WindowFrame frame = new(
            WindowFrameType.Rows,
            new UnboundedPrecedingBound(),
            new CurrentRowBound());

        DataValue[] results = ComputeWindow(function, rows,
            argumentExpressions: arguments, frame: frame);

        Assert.Equal(10f, results[0].AsFloat32());
        Assert.Equal(30f, results[1].AsFloat32());
        Assert.Equal(60f, results[2].AsFloat32());
    }

    [Fact]
    public void AggregateWindowAdapter_Count_WholePartition()
    {
        AggregateWindowAdapter function = new(new CountFunction());
        List<Row> rows =
        [
            MakeRow(("val", DataValue.FromFloat32(10f))),
            MakeRow(("val", DataValue.FromFloat32(20f))),
            MakeRow(("val", DataValue.FromFloat32(30f))),
        ];

        IReadOnlyList<Expression> arguments = [new ColumnReference("val")];
        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments);

        Assert.Equal(3f, results[0].AsFloat32());
        Assert.Equal(3f, results[1].AsFloat32());
        Assert.Equal(3f, results[2].AsFloat32());
    }

    [Fact]
    public void AggregateWindowAdapter_Avg_SlidingWindow()
    {
        AggregateWindowAdapter function = new(new AvgFunction());
        List<Row> rows =
        [
            MakeRow(("val", DataValue.FromFloat32(10f))),
            MakeRow(("val", DataValue.FromFloat32(20f))),
            MakeRow(("val", DataValue.FromFloat32(30f))),
            MakeRow(("val", DataValue.FromFloat32(40f))),
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
        Assert.Equal(15f, results[0].AsFloat32());
        // Row 1: avg(10,20,30) = 20
        Assert.Equal(20f, results[1].AsFloat32());
        // Row 2: avg(20,30,40) = 30
        Assert.Equal(30f, results[2].AsFloat32());
        // Row 3: avg(30,40) = 35
        Assert.Equal(35f, results[3].AsFloat32());
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
        Assert.NotNull(registry.TryGetWindow("FIRST_VALUE"));
        Assert.NotNull(registry.TryGetWindow("LAST_VALUE"));
        Assert.NotNull(registry.TryGetWindow("NTH_VALUE"));
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
        Assert.Contains("FIRST_VALUE", names);
        Assert.Contains("LAST_VALUE", names);
        Assert.Contains("NTH_VALUE", names);
    }

    // ─────────────── FIRST_VALUE ───────────────

    [Fact]
    public void FirstValue_WholePartition_ReturnsFirstRow()
    {
        FirstValueFunction function = new();
        List<Row> rows =
        [
            MakeRow(("val", DataValue.FromFloat32(10f))),
            MakeRow(("val", DataValue.FromFloat32(20f))),
            MakeRow(("val", DataValue.FromFloat32(30f))),
        ];

        IReadOnlyList<Expression> arguments = [new ColumnReference("val")];
        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments);

        Assert.Equal(10f, results[0].AsFloat32());
        Assert.Equal(10f, results[1].AsFloat32());
        Assert.Equal(10f, results[2].AsFloat32());
    }

    [Fact]
    public void FirstValue_WithRunningFrame_ReturnsFirstOfFrame()
    {
        FirstValueFunction function = new();
        List<Row> rows =
        [
            MakeRow(("val", DataValue.FromFloat32(10f))),
            MakeRow(("val", DataValue.FromFloat32(20f))),
            MakeRow(("val", DataValue.FromFloat32(30f))),
        ];

        IReadOnlyList<Expression> arguments = [new ColumnReference("val")];

        // ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
        WindowFrame frame = new(
            WindowFrameType.Rows,
            new UnboundedPrecedingBound(),
            new CurrentRowBound());

        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments, frame: frame);

        // First value of the running frame is always the first partition row.
        Assert.Equal(10f, results[0].AsFloat32());
        Assert.Equal(10f, results[1].AsFloat32());
        Assert.Equal(10f, results[2].AsFloat32());
    }

    [Fact]
    public void FirstValue_WithSlidingFrame_ReturnsFirstOfEachFrame()
    {
        FirstValueFunction function = new();
        List<Row> rows =
        [
            MakeRow(("val", DataValue.FromFloat32(10f))),
            MakeRow(("val", DataValue.FromFloat32(20f))),
            MakeRow(("val", DataValue.FromFloat32(30f))),
            MakeRow(("val", DataValue.FromFloat32(40f))),
        ];

        IReadOnlyList<Expression> arguments = [new ColumnReference("val")];

        // ROWS BETWEEN 1 PRECEDING AND 1 FOLLOWING
        WindowFrame frame = new(
            WindowFrameType.Rows,
            new PrecedingBound(1),
            new FollowingBound(1));

        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments, frame: frame);

        // Row 0: frame [0,1] → first = 10
        Assert.Equal(10f, results[0].AsFloat32());
        // Row 1: frame [0,2] → first = 10
        Assert.Equal(10f, results[1].AsFloat32());
        // Row 2: frame [1,3] → first = 20
        Assert.Equal(20f, results[2].AsFloat32());
        // Row 3: frame [2,3] → first = 30
        Assert.Equal(30f, results[3].AsFloat32());
    }

    [Fact]
    public void FirstValue_IgnoreNulls_SkipsNullValues()
    {
        FirstValueFunction function = new();
        List<Row> rows =
        [
            MakeRow(("val", DataValue.Null(DataKind.Float32))),
            MakeRow(("val", DataValue.Null(DataKind.Float32))),
            MakeRow(("val", DataValue.FromFloat32(30f))),
            MakeRow(("val", DataValue.FromFloat32(40f))),
        ];

        IReadOnlyList<Expression> arguments = [new ColumnReference("val")];
        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments,
            nullHandling: NullHandling.IgnoreNulls);

        Assert.Equal(30f, results[0].AsFloat32());
        Assert.Equal(30f, results[1].AsFloat32());
        Assert.Equal(30f, results[2].AsFloat32());
        Assert.Equal(30f, results[3].AsFloat32());
    }

    [Fact]
    public void FirstValue_IgnoreNulls_AllNulls_ReturnsNull()
    {
        FirstValueFunction function = new();
        List<Row> rows =
        [
            MakeRow(("val", DataValue.Null(DataKind.Float32))),
            MakeRow(("val", DataValue.Null(DataKind.Float32))),
        ];

        IReadOnlyList<Expression> arguments = [new ColumnReference("val")];
        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments,
            nullHandling: NullHandling.IgnoreNulls);

        Assert.True(results[0].IsNull);
        Assert.True(results[1].IsNull);
    }

    [Fact]
    public void FirstValue_SingleRow()
    {
        FirstValueFunction function = new();
        List<Row> rows =
        [
            MakeRow(("val", DataValue.FromFloat32(42f))),
        ];

        IReadOnlyList<Expression> arguments = [new ColumnReference("val")];
        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments);

        Assert.Equal(42f, results[0].AsFloat32());
    }

    [Fact]
    public void FirstValue_InvalidArgumentCount_Throws()
    {
        FirstValueFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([]));
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Float32, DataKind.Float32]));
    }

    // ─────────────── LAST_VALUE ───────────────

    [Fact]
    public void LastValue_WholePartition_ReturnsLastRow()
    {
        LastValueFunction function = new();
        List<Row> rows =
        [
            MakeRow(("val", DataValue.FromFloat32(10f))),
            MakeRow(("val", DataValue.FromFloat32(20f))),
            MakeRow(("val", DataValue.FromFloat32(30f))),
        ];

        IReadOnlyList<Expression> arguments = [new ColumnReference("val")];
        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments);

        Assert.Equal(30f, results[0].AsFloat32());
        Assert.Equal(30f, results[1].AsFloat32());
        Assert.Equal(30f, results[2].AsFloat32());
    }

    [Fact]
    public void LastValue_WithRunningFrame_ReturnsCurrentRow()
    {
        LastValueFunction function = new();
        List<Row> rows =
        [
            MakeRow(("val", DataValue.FromFloat32(10f))),
            MakeRow(("val", DataValue.FromFloat32(20f))),
            MakeRow(("val", DataValue.FromFloat32(30f))),
        ];

        IReadOnlyList<Expression> arguments = [new ColumnReference("val")];

        // ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW — last value = current row
        WindowFrame frame = new(
            WindowFrameType.Rows,
            new UnboundedPrecedingBound(),
            new CurrentRowBound());

        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments, frame: frame);

        Assert.Equal(10f, results[0].AsFloat32());
        Assert.Equal(20f, results[1].AsFloat32());
        Assert.Equal(30f, results[2].AsFloat32());
    }

    [Fact]
    public void LastValue_IgnoreNulls_SkipsNullValues()
    {
        LastValueFunction function = new();
        List<Row> rows =
        [
            MakeRow(("val", DataValue.FromFloat32(10f))),
            MakeRow(("val", DataValue.FromFloat32(20f))),
            MakeRow(("val", DataValue.Null(DataKind.Float32))),
            MakeRow(("val", DataValue.Null(DataKind.Float32))),
        ];

        IReadOnlyList<Expression> arguments = [new ColumnReference("val")];
        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments,
            nullHandling: NullHandling.IgnoreNulls);

        Assert.Equal(20f, results[0].AsFloat32());
        Assert.Equal(20f, results[1].AsFloat32());
        Assert.Equal(20f, results[2].AsFloat32());
        Assert.Equal(20f, results[3].AsFloat32());
    }

    [Fact]
    public void LastValue_SingleRow()
    {
        LastValueFunction function = new();
        List<Row> rows =
        [
            MakeRow(("val", DataValue.FromFloat32(99f))),
        ];

        IReadOnlyList<Expression> arguments = [new ColumnReference("val")];
        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments);

        Assert.Equal(99f, results[0].AsFloat32());
    }

    [Fact]
    public void LastValue_InvalidArgumentCount_Throws()
    {
        LastValueFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([]));
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Float32, DataKind.Float32]));
    }

    // ─────────────── NTH_VALUE ───────────────

    [Fact]
    public void NthValue_ReturnsNthRow()
    {
        NthValueFunction function = new();
        List<Row> rows =
        [
            MakeRow(("val", DataValue.FromFloat32(10f))),
            MakeRow(("val", DataValue.FromFloat32(20f))),
            MakeRow(("val", DataValue.FromFloat32(30f))),
        ];

        // NTH_VALUE(val, 2)
        IReadOnlyList<Expression> arguments =
        [
            new ColumnReference("val"),
            new LiteralExpression(2),
        ];

        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments);

        // All rows see the same whole-partition frame, so 2nd value = 20 for all.
        Assert.Equal(20f, results[0].AsFloat32());
        Assert.Equal(20f, results[1].AsFloat32());
        Assert.Equal(20f, results[2].AsFloat32());
    }

    [Fact]
    public void NthValue_N1_EqualsFirstValue()
    {
        NthValueFunction function = new();
        List<Row> rows =
        [
            MakeRow(("val", DataValue.FromFloat32(10f))),
            MakeRow(("val", DataValue.FromFloat32(20f))),
        ];

        IReadOnlyList<Expression> arguments =
        [
            new ColumnReference("val"),
            new LiteralExpression(1),
        ];

        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments);

        Assert.Equal(10f, results[0].AsFloat32());
        Assert.Equal(10f, results[1].AsFloat32());
    }

    [Fact]
    public void NthValue_ExceedsFrameSize_ReturnsNull()
    {
        NthValueFunction function = new();
        List<Row> rows =
        [
            MakeRow(("val", DataValue.FromFloat32(10f))),
            MakeRow(("val", DataValue.FromFloat32(20f))),
        ];

        // NTH_VALUE(val, 5) — only 2 rows
        IReadOnlyList<Expression> arguments =
        [
            new ColumnReference("val"),
            new LiteralExpression(5),
        ];

        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments);

        Assert.True(results[0].IsNull);
        Assert.True(results[1].IsNull);
    }

    [Fact]
    public void NthValue_FromLast_CountsBackward()
    {
        NthValueFunction function = new();
        List<Row> rows =
        [
            MakeRow(("val", DataValue.FromFloat32(10f))),
            MakeRow(("val", DataValue.FromFloat32(20f))),
            MakeRow(("val", DataValue.FromFloat32(30f))),
        ];

        // NTH_VALUE(val, 1) FROM LAST → last row = 30
        IReadOnlyList<Expression> arguments =
        [
            new ColumnReference("val"),
            new LiteralExpression(1),
        ];

        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments,
            fromLast: true);

        Assert.Equal(30f, results[0].AsFloat32());
        Assert.Equal(30f, results[1].AsFloat32());
        Assert.Equal(30f, results[2].AsFloat32());
    }

    [Fact]
    public void NthValue_FromLast_N2_ReturnsSecondToLast()
    {
        NthValueFunction function = new();
        List<Row> rows =
        [
            MakeRow(("val", DataValue.FromFloat32(10f))),
            MakeRow(("val", DataValue.FromFloat32(20f))),
            MakeRow(("val", DataValue.FromFloat32(30f))),
        ];

        // NTH_VALUE(val, 2) FROM LAST → second-to-last = 20
        IReadOnlyList<Expression> arguments =
        [
            new ColumnReference("val"),
            new LiteralExpression(2),
        ];

        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments,
            fromLast: true);

        Assert.Equal(20f, results[0].AsFloat32());
        Assert.Equal(20f, results[1].AsFloat32());
        Assert.Equal(20f, results[2].AsFloat32());
    }

    [Fact]
    public void NthValue_IgnoreNulls_SkipsNullValues()
    {
        NthValueFunction function = new();
        List<Row> rows =
        [
            MakeRow(("val", DataValue.Null(DataKind.Float32))),
            MakeRow(("val", DataValue.FromFloat32(10f))),
            MakeRow(("val", DataValue.Null(DataKind.Float32))),
            MakeRow(("val", DataValue.FromFloat32(20f))),
        ];

        // NTH_VALUE(val, 2) IGNORE NULLS → 2nd non-null = 20
        IReadOnlyList<Expression> arguments =
        [
            new ColumnReference("val"),
            new LiteralExpression(2),
        ];

        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments,
            nullHandling: NullHandling.IgnoreNulls);

        Assert.Equal(20f, results[0].AsFloat32());
        Assert.Equal(20f, results[1].AsFloat32());
        Assert.Equal(20f, results[2].AsFloat32());
        Assert.Equal(20f, results[3].AsFloat32());
    }

    [Fact]
    public void NthValue_WithFrame_RespectsFrameBounds()
    {
        NthValueFunction function = new();
        List<Row> rows =
        [
            MakeRow(("val", DataValue.FromFloat32(10f))),
            MakeRow(("val", DataValue.FromFloat32(20f))),
            MakeRow(("val", DataValue.FromFloat32(30f))),
            MakeRow(("val", DataValue.FromFloat32(40f))),
        ];

        // NTH_VALUE(val, 2) with ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING
        IReadOnlyList<Expression> arguments =
        [
            new ColumnReference("val"),
            new LiteralExpression(2),
        ];

        WindowFrame frame = new(
            WindowFrameType.Rows,
            new CurrentRowBound(),
            new UnboundedFollowingBound());

        DataValue[] results = ComputeWindow(function, rows, argumentExpressions: arguments, frame: frame);

        // Row 0: frame [0,3], 2nd = 20
        Assert.Equal(20f, results[0].AsFloat32());
        // Row 1: frame [1,3], 2nd = 30
        Assert.Equal(30f, results[1].AsFloat32());
        // Row 2: frame [2,3], 2nd = 40
        Assert.Equal(40f, results[2].AsFloat32());
        // Row 3: frame [3,3], only 1 row, 2nd = NULL
        Assert.True(results[3].IsNull);
    }

    [Fact]
    public void NthValue_InvalidArgumentCount_Throws()
    {
        NthValueFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([DataKind.Float32]));
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Float32, DataKind.Float32, DataKind.Float32]));
    }

    [Fact]
    public void NthValue_NonScalarN_Throws()
    {
        NthValueFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Float32, DataKind.String]));
    }
}
