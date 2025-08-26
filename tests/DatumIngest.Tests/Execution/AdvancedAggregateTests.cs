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
/// Tests for advanced aggregate functions: PERCENTILE_DISC, MODE, CORR,
/// COVAR_POP, COVAR_SAMP, APPROX_MEDIAN, APPROX_PERCENTILE, STRING_AGG, and ARRAY_AGG.
/// </summary>
public class AdvancedAggregateTests
{
    private static ExecutionContext CreateContext()
    {
        return new ExecutionContext(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            new TableCatalog(),
            new RowBufferPool());
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

    // ─────────────── PERCENTILE_DISC ───────────────

    [Fact]
    public async Task PercentileDisc_P50_ReturnsNearestRank()
    {
        // {1, 2, 3, 4, 5} → P50 nearest-rank: ceil(0.5 * 5) - 1 = index 2 → value 3
        MockOperator source = new(
            MakeRow(("x", DataValue.FromFloat32(3f)), ("p", DataValue.FromFloat32(0.5f))),
            MakeRow(("x", DataValue.FromFloat32(1f)), ("p", DataValue.FromFloat32(0.5f))),
            MakeRow(("x", DataValue.FromFloat32(5f)), ("p", DataValue.FromFloat32(0.5f))),
            MakeRow(("x", DataValue.FromFloat32(2f)), ("p", DataValue.FromFloat32(0.5f))),
            MakeRow(("x", DataValue.FromFloat32(4f)), ("p", DataValue.FromFloat32(0.5f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new PercentileDiscreteFunction(),
                    [new ColumnReference("x"), new ColumnReference("p")],
                    "PERCENTILE_DISC(x, 0.5)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(3f, results[0]["PERCENTILE_DISC(x, 0.5)"].AsFloat32());
    }

    [Fact]
    public async Task PercentileDisc_P0_ReturnsMinimum()
    {
        MockOperator source = new(
            MakeRow(("x", DataValue.FromFloat32(10f)), ("p", DataValue.FromFloat32(0f))),
            MakeRow(("x", DataValue.FromFloat32(20f)), ("p", DataValue.FromFloat32(0f))),
            MakeRow(("x", DataValue.FromFloat32(30f)), ("p", DataValue.FromFloat32(0f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new PercentileDiscreteFunction(),
                    [new ColumnReference("x"), new ColumnReference("p")],
                    "result"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(10f, results[0]["result"].AsFloat32());
    }

    [Fact]
    public async Task PercentileDisc_P100_ReturnsMaximum()
    {
        MockOperator source = new(
            MakeRow(("x", DataValue.FromFloat32(10f)), ("p", DataValue.FromFloat32(1f))),
            MakeRow(("x", DataValue.FromFloat32(20f)), ("p", DataValue.FromFloat32(1f))),
            MakeRow(("x", DataValue.FromFloat32(30f)), ("p", DataValue.FromFloat32(1f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new PercentileDiscreteFunction(),
                    [new ColumnReference("x"), new ColumnReference("p")],
                    "result"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(30f, results[0]["result"].AsFloat32());
    }

    [Fact]
    public async Task PercentileDisc_AllNull_ReturnsNull()
    {
        MockOperator source = new(
            MakeRow(("x", DataValue.Null(DataKind.Float32)), ("p", DataValue.FromFloat32(0.5f))),
            MakeRow(("x", DataValue.Null(DataKind.Float32)), ("p", DataValue.FromFloat32(0.5f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new PercentileDiscreteFunction(),
                    [new ColumnReference("x"), new ColumnReference("p")],
                    "result"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.True(results[0]["result"].IsNull);
    }

    [Fact]
    public async Task PercentileDisc_ReturnsObservedValue()
    {
        // {10, 20, 30, 40} → P25 nearest-rank: ceil(0.25 * 4) - 1 = index 0 → 10
        // Unlike PERCENTILE_CONT which would interpolate, DISC returns actual value
        MockOperator source = new(
            MakeRow(("x", DataValue.FromFloat32(40f)), ("p", DataValue.FromFloat32(0.25f))),
            MakeRow(("x", DataValue.FromFloat32(10f)), ("p", DataValue.FromFloat32(0.25f))),
            MakeRow(("x", DataValue.FromFloat32(30f)), ("p", DataValue.FromFloat32(0.25f))),
            MakeRow(("x", DataValue.FromFloat32(20f)), ("p", DataValue.FromFloat32(0.25f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new PercentileDiscreteFunction(),
                    [new ColumnReference("x"), new ColumnReference("p")],
                    "result"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        // Must be one of {10, 20, 30, 40}
        float value = results[0]["result"].AsFloat32();
        Assert.Contains(value, new[] { 10f, 20f, 30f, 40f });
    }

    // ─────────────── MODE ───────────────

    [Fact]
    public async Task Mode_ClearWinner()
    {
        // {1, 2, 2, 3} → mode = 2
        MockOperator source = new(
            MakeRow(("x", DataValue.FromFloat32(1f))),
            MakeRow(("x", DataValue.FromFloat32(2f))),
            MakeRow(("x", DataValue.FromFloat32(2f))),
            MakeRow(("x", DataValue.FromFloat32(3f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new ModeFunction(),
                    [new ColumnReference("x")],
                    "MODE(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(2f, results[0]["MODE(x)"].AsFloat32());
    }

    [Fact]
    public async Task Mode_Tie_ReturnsFirstSeen()
    {
        // {1, 2, 3} → all frequency 1, first-seen = 1
        MockOperator source = new(
            MakeRow(("x", DataValue.FromFloat32(1f))),
            MakeRow(("x", DataValue.FromFloat32(2f))),
            MakeRow(("x", DataValue.FromFloat32(3f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new ModeFunction(),
                    [new ColumnReference("x")],
                    "MODE(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(1f, results[0]["MODE(x)"].AsFloat32());
    }

    [Fact]
    public async Task Mode_StringValues()
    {
        // {"a", "b", "b", "c"} → mode = "b"
        MockOperator source = new(
            MakeRow(("x", DataValue.FromString("a"))),
            MakeRow(("x", DataValue.FromString("b"))),
            MakeRow(("x", DataValue.FromString("b"))),
            MakeRow(("x", DataValue.FromString("c"))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new ModeFunction(),
                    [new ColumnReference("x")],
                    "MODE(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal("b", results[0]["MODE(x)"].AsString());
    }

    [Fact]
    public async Task Mode_AllNull_ReturnsNull()
    {
        MockOperator source = new(
            MakeRow(("x", DataValue.Null(DataKind.Float32))),
            MakeRow(("x", DataValue.Null(DataKind.Float32))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new ModeFunction(),
                    [new ColumnReference("x")],
                    "MODE(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.True(results[0]["MODE(x)"].IsNull);
    }

    [Fact]
    public async Task Mode_PerGroup()
    {
        // Group A: {10, 10, 20} → mode = 10
        // Group B: {5, 5, 5}   → mode = 5
        MockOperator source = new(
            MakeRow(("cat", DataValue.FromString("A")), ("x", DataValue.FromFloat32(10f))),
            MakeRow(("cat", DataValue.FromString("B")), ("x", DataValue.FromFloat32(5f))),
            MakeRow(("cat", DataValue.FromString("A")), ("x", DataValue.FromFloat32(10f))),
            MakeRow(("cat", DataValue.FromString("B")), ("x", DataValue.FromFloat32(5f))),
            MakeRow(("cat", DataValue.FromString("A")), ("x", DataValue.FromFloat32(20f))),
            MakeRow(("cat", DataValue.FromString("B")), ("x", DataValue.FromFloat32(5f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("cat")],
            aggregateColumns:
            [
                new AggregateColumn(
                    new ModeFunction(),
                    [new ColumnReference("x")],
                    "MODE(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Equal(2, results.Count);

        Row groupA = results.First(row => row["cat"].AsString() == "A");
        Row groupB = results.First(row => row["cat"].AsString() == "B");

        Assert.Equal(10f, groupA["MODE(x)"].AsFloat32());
        Assert.Equal(5f, groupB["MODE(x)"].AsFloat32());
    }

    [Fact]
    public async Task Mode_SkipsNullValues()
    {
        // Non-null: {1, 2, 2} → mode = 2
        MockOperator source = new(
            MakeRow(("x", DataValue.FromFloat32(1f))),
            MakeRow(("x", DataValue.Null(DataKind.Float32))),
            MakeRow(("x", DataValue.FromFloat32(2f))),
            MakeRow(("x", DataValue.FromFloat32(2f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new ModeFunction(),
                    [new ColumnReference("x")],
                    "MODE(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(2f, results[0]["MODE(x)"].AsFloat32());
    }

    // ─────────────── CORR ───────────────

    [Fact]
    public async Task Corr_PerfectPositive()
    {
        // y = x → correlation = 1.0
        MockOperator source = new(
            MakeRow(("y", DataValue.FromFloat32(1f)), ("x", DataValue.FromFloat32(1f))),
            MakeRow(("y", DataValue.FromFloat32(2f)), ("x", DataValue.FromFloat32(2f))),
            MakeRow(("y", DataValue.FromFloat32(3f)), ("x", DataValue.FromFloat32(3f))),
            MakeRow(("y", DataValue.FromFloat32(4f)), ("x", DataValue.FromFloat32(4f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new CorrelationFunction(),
                    [new ColumnReference("y"), new ColumnReference("x")],
                    "CORR(y, x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(1.0f, results[0]["CORR(y, x)"].AsFloat32(), 0.001f);
    }

    [Fact]
    public async Task Corr_PerfectNegative()
    {
        // y = -x → correlation = -1.0
        MockOperator source = new(
            MakeRow(("y", DataValue.FromFloat32(-1f)), ("x", DataValue.FromFloat32(1f))),
            MakeRow(("y", DataValue.FromFloat32(-2f)), ("x", DataValue.FromFloat32(2f))),
            MakeRow(("y", DataValue.FromFloat32(-3f)), ("x", DataValue.FromFloat32(3f))),
            MakeRow(("y", DataValue.FromFloat32(-4f)), ("x", DataValue.FromFloat32(4f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new CorrelationFunction(),
                    [new ColumnReference("y"), new ColumnReference("x")],
                    "CORR(y, x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(-1.0f, results[0]["CORR(y, x)"].AsFloat32(), 0.001f);
    }

    [Fact]
    public async Task Corr_ZeroVariance_ReturnsNull()
    {
        // y is constant → zero variance → null
        MockOperator source = new(
            MakeRow(("y", DataValue.FromFloat32(5f)), ("x", DataValue.FromFloat32(1f))),
            MakeRow(("y", DataValue.FromFloat32(5f)), ("x", DataValue.FromFloat32(2f))),
            MakeRow(("y", DataValue.FromFloat32(5f)), ("x", DataValue.FromFloat32(3f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new CorrelationFunction(),
                    [new ColumnReference("y"), new ColumnReference("x")],
                    "CORR(y, x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.True(results[0]["CORR(y, x)"].IsNull);
    }

    [Fact]
    public async Task Corr_SinglePair_ReturnsNull()
    {
        MockOperator source = new(
            MakeRow(("y", DataValue.FromFloat32(1f)), ("x", DataValue.FromFloat32(2f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new CorrelationFunction(),
                    [new ColumnReference("y"), new ColumnReference("x")],
                    "CORR(y, x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.True(results[0]["CORR(y, x)"].IsNull);
    }

    [Fact]
    public async Task Corr_AllNull_ReturnsNull()
    {
        MockOperator source = new(
            MakeRow(("y", DataValue.Null(DataKind.Float32)), ("x", DataValue.Null(DataKind.Float32))),
            MakeRow(("y", DataValue.Null(DataKind.Float32)), ("x", DataValue.Null(DataKind.Float32))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new CorrelationFunction(),
                    [new ColumnReference("y"), new ColumnReference("x")],
                    "CORR(y, x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.True(results[0]["CORR(y, x)"].IsNull);
    }

    [Fact]
    public async Task Corr_KnownValue()
    {
        // y = {1, 2, 3, 4, 5}, x = {2, 4, 5, 4, 5}
        // Known Pearson r ≈ 0.7746
        MockOperator source = new(
            MakeRow(("y", DataValue.FromFloat32(1f)), ("x", DataValue.FromFloat32(2f))),
            MakeRow(("y", DataValue.FromFloat32(2f)), ("x", DataValue.FromFloat32(4f))),
            MakeRow(("y", DataValue.FromFloat32(3f)), ("x", DataValue.FromFloat32(5f))),
            MakeRow(("y", DataValue.FromFloat32(4f)), ("x", DataValue.FromFloat32(4f))),
            MakeRow(("y", DataValue.FromFloat32(5f)), ("x", DataValue.FromFloat32(5f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new CorrelationFunction(),
                    [new ColumnReference("y"), new ColumnReference("x")],
                    "CORR(y, x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(0.7746f, results[0]["CORR(y, x)"].AsFloat32(), 0.01f);
    }

    // ─────────────── COVAR_POP / COVAR_SAMP ───────────────

    [Fact]
    public async Task CovarPopulation_KnownValue()
    {
        // y = {1, 2, 3, 4, 5}, x = {2, 4, 6, 8, 10}
        // meanY = 3, meanX = 6
        // COVAR_POP = Σ((yi - 3)(xi - 6)) / 5
        //           = ((1-3)(2-6) + (2-3)(4-6) + (3-3)(6-6) + (4-3)(8-6) + (5-3)(10-6)) / 5
        //           = (8 + 2 + 0 + 2 + 8) / 5 = 20/5 = 4.0
        MockOperator source = new(
            MakeRow(("y", DataValue.FromFloat32(1f)), ("x", DataValue.FromFloat32(2f))),
            MakeRow(("y", DataValue.FromFloat32(2f)), ("x", DataValue.FromFloat32(4f))),
            MakeRow(("y", DataValue.FromFloat32(3f)), ("x", DataValue.FromFloat32(6f))),
            MakeRow(("y", DataValue.FromFloat32(4f)), ("x", DataValue.FromFloat32(8f))),
            MakeRow(("y", DataValue.FromFloat32(5f)), ("x", DataValue.FromFloat32(10f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new CovarianceFunction(usePopulation: true, "COVAR_POP"),
                    [new ColumnReference("y"), new ColumnReference("x")],
                    "COVAR_POP(y, x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(4.0f, results[0]["COVAR_POP(y, x)"].AsFloat32(), 0.001f);
    }

    [Fact]
    public async Task CovarSample_KnownValue()
    {
        // Same data as above
        // COVAR_SAMP = Σ((yi - 3)(xi - 6)) / (5 - 1) = 20/4 = 5.0
        MockOperator source = new(
            MakeRow(("y", DataValue.FromFloat32(1f)), ("x", DataValue.FromFloat32(2f))),
            MakeRow(("y", DataValue.FromFloat32(2f)), ("x", DataValue.FromFloat32(4f))),
            MakeRow(("y", DataValue.FromFloat32(3f)), ("x", DataValue.FromFloat32(6f))),
            MakeRow(("y", DataValue.FromFloat32(4f)), ("x", DataValue.FromFloat32(8f))),
            MakeRow(("y", DataValue.FromFloat32(5f)), ("x", DataValue.FromFloat32(10f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new CovarianceFunction(usePopulation: false, "COVAR_SAMP"),
                    [new ColumnReference("y"), new ColumnReference("x")],
                    "COVAR_SAMP(y, x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(5.0f, results[0]["COVAR_SAMP(y, x)"].AsFloat32(), 0.001f);
    }

    [Fact]
    public async Task CovarSample_SinglePair_ReturnsNull()
    {
        MockOperator source = new(
            MakeRow(("y", DataValue.FromFloat32(1f)), ("x", DataValue.FromFloat32(2f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new CovarianceFunction(usePopulation: false, "COVAR_SAMP"),
                    [new ColumnReference("y"), new ColumnReference("x")],
                    "COVAR_SAMP(y, x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.True(results[0]["COVAR_SAMP(y, x)"].IsNull);
    }

    [Fact]
    public async Task CovarPopulation_SinglePair_ReturnsZero()
    {
        // Single pair: coMoment = 0, population = 0/1 = 0
        MockOperator source = new(
            MakeRow(("y", DataValue.FromFloat32(1f)), ("x", DataValue.FromFloat32(2f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new CovarianceFunction(usePopulation: true, "COVAR_POP"),
                    [new ColumnReference("y"), new ColumnReference("x")],
                    "COVAR_POP(y, x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(0f, results[0]["COVAR_POP(y, x)"].AsFloat32());
    }

    [Fact]
    public async Task Covar_AllNull_ReturnsNull()
    {
        MockOperator source = new(
            MakeRow(("y", DataValue.Null(DataKind.Float32)), ("x", DataValue.Null(DataKind.Float32))),
            MakeRow(("y", DataValue.Null(DataKind.Float32)), ("x", DataValue.Null(DataKind.Float32))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new CovarianceFunction(usePopulation: true, "COVAR_POP"),
                    [new ColumnReference("y"), new ColumnReference("x")],
                    "COVAR_POP(y, x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.True(results[0]["COVAR_POP(y, x)"].IsNull);
    }

    // ─────────────── APPROX_MEDIAN ───────────────

    [Fact]
    public async Task ApproxMedian_SmallDataset_ExactResult()
    {
        // With fewer values than the reservoir cap, result is exact
        // {1, 3, 5} → exact median = 3
        MockOperator source = new(
            MakeRow(("x", DataValue.FromFloat32(5f))),
            MakeRow(("x", DataValue.FromFloat32(1f))),
            MakeRow(("x", DataValue.FromFloat32(3f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new ApproximateMedianFunction(),
                    [new ColumnReference("x")],
                    "APPROX_MEDIAN(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(3f, results[0]["APPROX_MEDIAN(x)"].AsFloat32());
    }

    [Fact]
    public async Task ApproxMedian_EvenCount_SmallDataset()
    {
        // {1, 3, 5, 7} → median = (3 + 5) / 2 = 4
        MockOperator source = new(
            MakeRow(("x", DataValue.FromFloat32(7f))),
            MakeRow(("x", DataValue.FromFloat32(1f))),
            MakeRow(("x", DataValue.FromFloat32(5f))),
            MakeRow(("x", DataValue.FromFloat32(3f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new ApproximateMedianFunction(),
                    [new ColumnReference("x")],
                    "APPROX_MEDIAN(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(4f, results[0]["APPROX_MEDIAN(x)"].AsFloat32());
    }

    [Fact]
    public async Task ApproxMedian_AllNull_ReturnsNull()
    {
        MockOperator source = new(
            MakeRow(("x", DataValue.Null(DataKind.Float32))),
            MakeRow(("x", DataValue.Null(DataKind.Float32))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new ApproximateMedianFunction(),
                    [new ColumnReference("x")],
                    "APPROX_MEDIAN(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.True(results[0]["APPROX_MEDIAN(x)"].IsNull);
    }

    // ─────────────── APPROX_PERCENTILE ───────────────

    [Fact]
    public async Task ApproxPercentile_SmallDataset_ExactResult()
    {
        // {1, 2, 3, 4, 5} → P50 exact = 3
        MockOperator source = new(
            MakeRow(("x", DataValue.FromFloat32(3f)), ("p", DataValue.FromFloat32(0.5f))),
            MakeRow(("x", DataValue.FromFloat32(1f)), ("p", DataValue.FromFloat32(0.5f))),
            MakeRow(("x", DataValue.FromFloat32(5f)), ("p", DataValue.FromFloat32(0.5f))),
            MakeRow(("x", DataValue.FromFloat32(2f)), ("p", DataValue.FromFloat32(0.5f))),
            MakeRow(("x", DataValue.FromFloat32(4f)), ("p", DataValue.FromFloat32(0.5f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new ApproximatePercentileFunction(),
                    [new ColumnReference("x"), new ColumnReference("p")],
                    "APPROX_PERCENTILE(x, 0.5)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(3f, results[0]["APPROX_PERCENTILE(x, 0.5)"].AsFloat32(), 0.001f);
    }

    [Fact]
    public async Task ApproxPercentile_P0_ReturnsMinimum()
    {
        MockOperator source = new(
            MakeRow(("x", DataValue.FromFloat32(10f)), ("p", DataValue.FromFloat32(0f))),
            MakeRow(("x", DataValue.FromFloat32(20f)), ("p", DataValue.FromFloat32(0f))),
            MakeRow(("x", DataValue.FromFloat32(30f)), ("p", DataValue.FromFloat32(0f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new ApproximatePercentileFunction(),
                    [new ColumnReference("x"), new ColumnReference("p")],
                    "result"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(10f, results[0]["result"].AsFloat32());
    }

    [Fact]
    public async Task ApproxPercentile_AllNull_ReturnsNull()
    {
        MockOperator source = new(
            MakeRow(("x", DataValue.Null(DataKind.Float32)), ("p", DataValue.FromFloat32(0.5f))),
            MakeRow(("x", DataValue.Null(DataKind.Float32)), ("p", DataValue.FromFloat32(0.5f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new ApproximatePercentileFunction(),
                    [new ColumnReference("x"), new ColumnReference("p")],
                    "result"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.True(results[0]["result"].IsNull);
    }

    // ─────────────── STRING_AGG ───────────────

    [Fact]
    public async Task StringAgg_BasicConcatenation()
    {
        MockOperator source = new(
            MakeRow(("x", DataValue.FromString("a")), ("sep", DataValue.FromString(", "))),
            MakeRow(("x", DataValue.FromString("b")), ("sep", DataValue.FromString(", "))),
            MakeRow(("x", DataValue.FromString("c")), ("sep", DataValue.FromString(", "))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new StringAggregateFunction(),
                    [new ColumnReference("x"), new ColumnReference("sep")],
                    "STRING_AGG(x, ', ')"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal("a, b, c", results[0]["STRING_AGG(x, ', ')"].AsString());
    }

    [Fact]
    public async Task StringAgg_AllNull_ReturnsNull()
    {
        MockOperator source = new(
            MakeRow(("x", DataValue.Null(DataKind.String)), ("sep", DataValue.FromString(","))),
            MakeRow(("x", DataValue.Null(DataKind.String)), ("sep", DataValue.FromString(","))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new StringAggregateFunction(),
                    [new ColumnReference("x"), new ColumnReference("sep")],
                    "result"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.True(results[0]["result"].IsNull);
    }

    [Fact]
    public async Task StringAgg_SkipsNullValues()
    {
        // Non-null values: "a", "c" → "a, c"
        MockOperator source = new(
            MakeRow(("x", DataValue.FromString("a")), ("sep", DataValue.FromString(", "))),
            MakeRow(("x", DataValue.Null(DataKind.String)), ("sep", DataValue.FromString(", "))),
            MakeRow(("x", DataValue.FromString("c")), ("sep", DataValue.FromString(", "))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new StringAggregateFunction(),
                    [new ColumnReference("x"), new ColumnReference("sep")],
                    "result"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal("a, c", results[0]["result"].AsString());
    }

    [Fact]
    public async Task StringAgg_PerGroup()
    {
        MockOperator source = new(
            MakeRow(("cat", DataValue.FromString("A")), ("x", DataValue.FromString("x")), ("sep", DataValue.FromString(","))),
            MakeRow(("cat", DataValue.FromString("B")), ("x", DataValue.FromString("p")), ("sep", DataValue.FromString(","))),
            MakeRow(("cat", DataValue.FromString("A")), ("x", DataValue.FromString("y")), ("sep", DataValue.FromString(","))),
            MakeRow(("cat", DataValue.FromString("B")), ("x", DataValue.FromString("q")), ("sep", DataValue.FromString(","))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("cat")],
            aggregateColumns:
            [
                new AggregateColumn(
                    new StringAggregateFunction(),
                    [new ColumnReference("x"), new ColumnReference("sep")],
                    "result"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Equal(2, results.Count);

        Row groupA = results.First(row => row["cat"].AsString() == "A");
        Row groupB = results.First(row => row["cat"].AsString() == "B");

        Assert.Equal("x,y", groupA["result"].AsString());
        Assert.Equal("p,q", groupB["result"].AsString());
    }

    [Fact]
    public async Task StringAgg_WithOrderByAscending()
    {
        // Without ORDER BY, insertion order gives "c, a, b"
        // With ORDER BY x ASC, result should be "a, b, c"
        MockOperator source = new(
            MakeRow(("x", DataValue.FromString("c")), ("sep", DataValue.FromString(", "))),
            MakeRow(("x", DataValue.FromString("a")), ("sep", DataValue.FromString(", "))),
            MakeRow(("x", DataValue.FromString("b")), ("sep", DataValue.FromString(", "))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new StringAggregateFunction(),
                    [new ColumnReference("x"), new ColumnReference("sep")],
                    "result",
                    OrderBy: [new OrderByItem(new ColumnReference("x"), SortDirection.Ascending)]),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal("a, b, c", results[0]["result"].AsString());
    }

    [Fact]
    public async Task StringAgg_WithOrderByDescending()
    {
        MockOperator source = new(
            MakeRow(("x", DataValue.FromString("c")), ("sep", DataValue.FromString(", "))),
            MakeRow(("x", DataValue.FromString("a")), ("sep", DataValue.FromString(", "))),
            MakeRow(("x", DataValue.FromString("b")), ("sep", DataValue.FromString(", "))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new StringAggregateFunction(),
                    [new ColumnReference("x"), new ColumnReference("sep")],
                    "result",
                    OrderBy: [new OrderByItem(new ColumnReference("x"), SortDirection.Descending)]),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal("c, b, a", results[0]["result"].AsString());
    }

    // ─────────────── ARRAY_AGG ───────────────

    [Fact]
    public async Task ArrayAgg_BasicCollection()
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
                new AggregateColumn(
                    new ArrayAggregateFunction(),
                    [new ColumnReference("x")],
                    "result"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        DataValue result = results[0]["result"];
        Assert.Equal(DataKind.Array, result.Kind);
        DataValue[] elements = result.AsArray();
        Assert.Equal(3, elements.Length);
        Assert.Equal(1f, elements[0].AsFloat32());
        Assert.Equal(2f, elements[1].AsFloat32());
        Assert.Equal(3f, elements[2].AsFloat32());
    }

    [Fact]
    public async Task ArrayAgg_AllNull_ReturnsNull()
    {
        MockOperator source = new(
            MakeRow(("x", DataValue.Null(DataKind.String))),
            MakeRow(("x", DataValue.Null(DataKind.String))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new ArrayAggregateFunction(),
                    [new ColumnReference("x")],
                    "result"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.True(results[0]["result"].IsNull);
    }

    [Fact]
    public async Task ArrayAgg_SkipsNullValues()
    {
        MockOperator source = new(
            MakeRow(("x", DataValue.FromString("a"))),
            MakeRow(("x", DataValue.Null(DataKind.String))),
            MakeRow(("x", DataValue.FromString("c"))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new ArrayAggregateFunction(),
                    [new ColumnReference("x")],
                    "result"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        DataValue[] elements = results[0]["result"].AsArray();
        Assert.Equal(2, elements.Length);
        Assert.Equal("a", elements[0].AsString());
        Assert.Equal("c", elements[1].AsString());
    }

    [Fact]
    public async Task ArrayAgg_PerGroup()
    {
        MockOperator source = new(
            MakeRow(("cat", DataValue.FromString("A")), ("x", DataValue.FromFloat32(1f))),
            MakeRow(("cat", DataValue.FromString("B")), ("x", DataValue.FromFloat32(10f))),
            MakeRow(("cat", DataValue.FromString("A")), ("x", DataValue.FromFloat32(2f))),
            MakeRow(("cat", DataValue.FromString("B")), ("x", DataValue.FromFloat32(20f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("cat")],
            aggregateColumns:
            [
                new AggregateColumn(
                    new ArrayAggregateFunction(),
                    [new ColumnReference("x")],
                    "result"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Equal(2, results.Count);

        Row groupA = results.First(row => row["cat"].AsString() == "A");
        Row groupB = results.First(row => row["cat"].AsString() == "B");

        DataValue[] elementsA = groupA["result"].AsArray();
        Assert.Equal(2, elementsA.Length);
        Assert.Equal(1f, elementsA[0].AsFloat32());
        Assert.Equal(2f, elementsA[1].AsFloat32());

        DataValue[] elementsB = groupB["result"].AsArray();
        Assert.Equal(2, elementsB.Length);
        Assert.Equal(10f, elementsB[0].AsFloat32());
        Assert.Equal(20f, elementsB[1].AsFloat32());
    }

    [Fact]
    public async Task ArrayAgg_WithOrderByAscending()
    {
        MockOperator source = new(
            MakeRow(("x", DataValue.FromFloat32(3f))),
            MakeRow(("x", DataValue.FromFloat32(1f))),
            MakeRow(("x", DataValue.FromFloat32(2f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new ArrayAggregateFunction(),
                    [new ColumnReference("x")],
                    "result",
                    OrderBy: [new OrderByItem(new ColumnReference("x"), SortDirection.Ascending)]),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        DataValue[] elements = results[0]["result"].AsArray();
        Assert.Equal(1f, elements[0].AsFloat32());
        Assert.Equal(2f, elements[1].AsFloat32());
        Assert.Equal(3f, elements[2].AsFloat32());
    }

    [Fact]
    public async Task ArrayAgg_WithOrderByDescending()
    {
        MockOperator source = new(
            MakeRow(("x", DataValue.FromFloat32(3f))),
            MakeRow(("x", DataValue.FromFloat32(1f))),
            MakeRow(("x", DataValue.FromFloat32(2f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new ArrayAggregateFunction(),
                    [new ColumnReference("x")],
                    "result",
                    OrderBy: [new OrderByItem(new ColumnReference("x"), SortDirection.Descending)]),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        DataValue[] elements = results[0]["result"].AsArray();
        Assert.Equal(3f, elements[0].AsFloat32());
        Assert.Equal(2f, elements[1].AsFloat32());
        Assert.Equal(1f, elements[2].AsFloat32());
    }

    [Fact]
    public async Task ArrayAgg_WithDistinct()
    {
        MockOperator source = new(
            MakeRow(("x", DataValue.FromString("a"))),
            MakeRow(("x", DataValue.FromString("b"))),
            MakeRow(("x", DataValue.FromString("a"))),
            MakeRow(("x", DataValue.FromString("c"))),
            MakeRow(("x", DataValue.FromString("b"))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new ArrayAggregateFunction(),
                    [new ColumnReference("x")],
                    "result",
                    Distinct: true),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        DataValue[] elements = results[0]["result"].AsArray();
        Assert.Equal(3, elements.Length);

        // DISTINCT should yield exactly {"a", "b", "c"} in some order
        string[] values = elements.Select(e => e.AsString()).Order().ToArray();
        Assert.Equal(["a", "b", "c"], values);
    }

    // ─────────────── REGISTRY ───────────────

    [Theory]
    [InlineData("PERCENTILE_DISC")]
    [InlineData("MODE")]
    [InlineData("CORR")]
    [InlineData("COVAR_POP")]
    [InlineData("COVAR_SAMP")]
    [InlineData("APPROX_MEDIAN")]
    [InlineData("APPROX_PERCENTILE")]
    [InlineData("STRING_AGG")]
    [InlineData("ARRAY_AGG")]
    public void Registry_ContainsFunction(string functionName)
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        IAggregateFunction? function = registry.TryGetAggregate(functionName);

        Assert.NotNull(function);
        Assert.Equal(functionName, function.Name);
    }
}
