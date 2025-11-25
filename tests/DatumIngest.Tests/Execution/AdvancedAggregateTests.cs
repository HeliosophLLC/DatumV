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
public class AdvancedAggregateTests : ServiceTestBase
{
    private static readonly string[] XpColumns = ["x", "p"];
    private static readonly string[] XColumns = ["x"];
    private static readonly string[] YxColumns = ["y", "x"];
    private static readonly string[] CatXColumns = ["cat", "x"];
    private static readonly string[] XSepColumns = ["x", "sep"];
    private static readonly string[] CatXSepColumns = ["cat", "x", "sep"];

    private async Task<List<Row>> CollectAsync(IQueryOperator op, ExecutionContext? context = null)
    {
        context ??= CreateExecutionContext();
        return await op.CollectRowsAsync(context);
    }

    // PERCENTILE_DISC

    [Fact]
    public async Task PercentileDisc_P50_ReturnsNearestRank()
    {
        // {1, 2, 3, 4, 5} -> P50 nearest-rank: ceil(0.5 * 5) - 1 = index 2 -> value 3
        MockOperator source = CreateMockOperator(XpColumns,
            [3f, 0.5f],
            [1f, 0.5f],
            [5f, 0.5f],
            [2f, 0.5f],
            [4f, 0.5f]);

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
        Assert.Equal(3.0, results[0]["PERCENTILE_DISC(x, 0.5)"].AsFloat64());
    }

    [Fact]
    public async Task PercentileDisc_P0_ReturnsMinimum()
    {
        MockOperator source = CreateMockOperator(XpColumns,
            [10f, 0f],
            [20f, 0f],
            [30f, 0f]);

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
        Assert.Equal(10.0, results[0]["result"].AsFloat64());
    }

    [Fact]
    public async Task PercentileDisc_P100_ReturnsMaximum()
    {
        MockOperator source = CreateMockOperator(XpColumns,
            [10f, 1f],
            [20f, 1f],
            [30f, 1f]);

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
        Assert.Equal(30.0, results[0]["result"].AsFloat64());
    }

    [Fact]
    public async Task PercentileDisc_AllNull_ReturnsNull()
    {
        MockOperator source = CreateMockOperator(XpColumns,
            [DataValue.Null(DataKind.Float32), 0.5f],
            [DataValue.Null(DataKind.Float32), 0.5f]);

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
        // {10, 20, 30, 40} -> P25 nearest-rank: ceil(0.25 * 4) - 1 = index 0 -> 10
        // Unlike PERCENTILE_CONT which would interpolate, DISC returns actual value
        MockOperator source = CreateMockOperator(XpColumns,
            [40f, 0.25f],
            [10f, 0.25f],
            [30f, 0.25f],
            [20f, 0.25f]);

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
        double value = results[0]["result"].AsFloat64();
        Assert.Contains(value, new[] { 10.0, 20.0, 30.0, 40.0 });
    }

    // MODE

    [Fact]
    public async Task Mode_ClearWinner()
    {
        // {1, 2, 2, 3} -> mode = 2
        MockOperator source = CreateMockOperator(XColumns,
            [1f],
            [2f],
            [2f],
            [3f]);

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
        // {1, 2, 3} -> all frequency 1, first-seen = 1
        MockOperator source = CreateMockOperator(XColumns,
            [1f],
            [2f],
            [3f]);

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
        // {"a", "b", "b", "c"} -> mode = "b"
        MockOperator source = CreateMockOperator(XColumns,
            ["a"],
            ["b"],
            ["b"],
            ["c"]);

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
        MockOperator source = CreateMockOperator(XColumns,
            [DataValue.Null(DataKind.Float32)],
            [DataValue.Null(DataKind.Float32)]);

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
        // Group A: {10, 10, 20} -> mode = 10
        // Group B: {5, 5, 5}    -> mode = 5
        MockOperator source = CreateMockOperator(CatXColumns,
            ["A", 10f],
            ["B", 5f],
            ["A", 10f],
            ["B", 5f],
            ["A", 20f],
            ["B", 5f]);

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
        // Non-null: {1, 2, 2} -> mode = 2
        MockOperator source = CreateMockOperator(XColumns,
            [1f],
            [DataValue.Null(DataKind.Float32)],
            [2f],
            [2f]);

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

    // CORR

    [Fact]
    public async Task Corr_PerfectPositive()
    {
        // y = x -> correlation = 1.0
        MockOperator source = CreateMockOperator(YxColumns,
            [1f, 1f],
            [2f, 2f],
            [3f, 3f],
            [4f, 4f]);

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
        Assert.Equal(1.0, results[0]["CORR(y, x)"].AsFloat64(), 0.001);
    }

    [Fact]
    public async Task Corr_PerfectNegative()
    {
        // y = -x -> correlation = -1.0
        MockOperator source = CreateMockOperator(YxColumns,
            [-1f, 1f],
            [-2f, 2f],
            [-3f, 3f],
            [-4f, 4f]);

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
        Assert.Equal(-1.0, results[0]["CORR(y, x)"].AsFloat64(), 0.001);
    }

    [Fact]
    public async Task Corr_ZeroVariance_ReturnsNull()
    {
        // y is constant -> zero variance -> null
        MockOperator source = CreateMockOperator(YxColumns,
            [5f, 1f],
            [5f, 2f],
            [5f, 3f]);

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
        MockOperator source = CreateMockOperator(YxColumns,
            [1f, 2f]);

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
        MockOperator source = CreateMockOperator(YxColumns,
            [DataValue.Null(DataKind.Float32), DataValue.Null(DataKind.Float32)],
            [DataValue.Null(DataKind.Float32), DataValue.Null(DataKind.Float32)]);

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
        // Known Pearson r ~ 0.7746
        MockOperator source = CreateMockOperator(YxColumns,
            [1f, 2f],
            [2f, 4f],
            [3f, 5f],
            [4f, 4f],
            [5f, 5f]);

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
        Assert.Equal(0.7746, results[0]["CORR(y, x)"].AsFloat64(), 0.01);
    }

    // COVAR_POP / COVAR_SAMP

    [Fact]
    public async Task CovarPopulation_KnownValue()
    {
        // y = {1, 2, 3, 4, 5}, x = {2, 4, 6, 8, 10}
        MockOperator source = CreateMockOperator(YxColumns,
            [1f, 2f],
            [2f, 4f],
            [3f, 6f],
            [4f, 8f],
            [5f, 10f]);

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
        Assert.Equal(4.0, results[0]["COVAR_POP(y, x)"].AsFloat64(), 0.001);
    }

    [Fact]
    public async Task CovarSample_KnownValue()
    {
        // Same data as above; sample covariance = 5.0
        MockOperator source = CreateMockOperator(YxColumns,
            [1f, 2f],
            [2f, 4f],
            [3f, 6f],
            [4f, 8f],
            [5f, 10f]);

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
        Assert.Equal(5.0, results[0]["COVAR_SAMP(y, x)"].AsFloat64(), 0.001);
    }

    [Fact]
    public async Task CovarSample_SinglePair_ReturnsNull()
    {
        MockOperator source = CreateMockOperator(YxColumns,
            [1f, 2f]);

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
        MockOperator source = CreateMockOperator(YxColumns,
            [1f, 2f]);

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
        Assert.Equal(0.0, results[0]["COVAR_POP(y, x)"].AsFloat64());
    }

    [Fact]
    public async Task Covar_AllNull_ReturnsNull()
    {
        MockOperator source = CreateMockOperator(YxColumns,
            [DataValue.Null(DataKind.Float32), DataValue.Null(DataKind.Float32)],
            [DataValue.Null(DataKind.Float32), DataValue.Null(DataKind.Float32)]);

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

    // APPROX_MEDIAN

    [Fact]
    public async Task ApproxMedian_SmallDataset_ExactResult()
    {
        // {1, 3, 5} -> exact median = 3
        MockOperator source = CreateMockOperator(XColumns,
            [5f],
            [1f],
            [3f]);

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
        Assert.Equal(3.0, results[0]["APPROX_MEDIAN(x)"].AsFloat64());
    }

    [Fact]
    public async Task ApproxMedian_EvenCount_SmallDataset()
    {
        // {1, 3, 5, 7} -> median = (3 + 5) / 2 = 4
        MockOperator source = CreateMockOperator(XColumns,
            [7f],
            [1f],
            [5f],
            [3f]);

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
        Assert.Equal(4.0, results[0]["APPROX_MEDIAN(x)"].AsFloat64());
    }

    [Fact]
    public async Task ApproxMedian_AllNull_ReturnsNull()
    {
        MockOperator source = CreateMockOperator(XColumns,
            [DataValue.Null(DataKind.Float32)],
            [DataValue.Null(DataKind.Float32)]);

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

    // APPROX_PERCENTILE

    [Fact]
    public async Task ApproxPercentile_SmallDataset_ExactResult()
    {
        // {1, 2, 3, 4, 5} -> P50 exact = 3
        MockOperator source = CreateMockOperator(XpColumns,
            [3f, 0.5f],
            [1f, 0.5f],
            [5f, 0.5f],
            [2f, 0.5f],
            [4f, 0.5f]);

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
        Assert.Equal(3.0, results[0]["APPROX_PERCENTILE(x, 0.5)"].AsFloat64(), 0.001);
    }

    [Fact]
    public async Task ApproxPercentile_P0_ReturnsMinimum()
    {
        MockOperator source = CreateMockOperator(XpColumns,
            [10f, 0f],
            [20f, 0f],
            [30f, 0f]);

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
        Assert.Equal(10.0, results[0]["result"].AsFloat64());
    }

    [Fact]
    public async Task ApproxPercentile_AllNull_ReturnsNull()
    {
        MockOperator source = CreateMockOperator(XpColumns,
            [DataValue.Null(DataKind.Float32), 0.5f],
            [DataValue.Null(DataKind.Float32), 0.5f]);

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

    // STRING_AGG

    [Fact]
    public async Task StringAgg_BasicConcatenation()
    {
        MockOperator source = CreateMockOperator(XSepColumns,
            ["a", ", "],
            ["b", ", "],
            ["c", ", "]);

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
        MockOperator source = CreateMockOperator(XSepColumns,
            [DataValue.Null(DataKind.String), ","],
            [DataValue.Null(DataKind.String), ","]);

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
        // Non-null values: "a", "c" -> "a, c"
        MockOperator source = CreateMockOperator(XSepColumns,
            ["a", ", "],
            [DataValue.Null(DataKind.String), ", "],
            ["c", ", "]);

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
        MockOperator source = CreateMockOperator(CatXSepColumns,
            ["A", "x", ","],
            ["B", "p", ","],
            ["A", "y", ","],
            ["B", "q", ","]);

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
        MockOperator source = CreateMockOperator(XSepColumns,
            ["c", ", "],
            ["a", ", "],
            ["b", ", "]);

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
        MockOperator source = CreateMockOperator(XSepColumns,
            ["c", ", "],
            ["a", ", "],
            ["b", ", "]);

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

    // ARRAY_AGG

    [Fact]
    public async Task ArrayAgg_BasicCollection()
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
                new AggregateColumn(
                    new ArrayAggregateFunction(),
                    [new ColumnReference("x")],
                    "result"),
            ]);

        ExecutionContext ctx = CreateExecutionContext();
        List<Row> results = await CollectAsync(groupBy, ctx);

        Assert.Single(results);
        DataValue result = results[0]["result"];
        // Typed array: Kind = element kind, IsArray flag set.
        Assert.Equal(DataKind.Float32, result.Kind);
        Assert.True(result.IsArray);
        ReadOnlySpan<float> elements = result.AsArraySpan<float>(ctx.Store);
        Assert.Equal(3, elements.Length);
        Assert.Equal(1f, elements[0]);
        Assert.Equal(2f, elements[1]);
        Assert.Equal(3f, elements[2]);
    }

    [Fact]
    public async Task ArrayAgg_AllNull_ReturnsNull()
    {
        MockOperator source = CreateMockOperator(XColumns,
            [DataValue.Null(DataKind.String)],
            [DataValue.Null(DataKind.String)]);

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
        MockOperator source = CreateMockOperator(XColumns,
            ["a"],
            [DataValue.Null(DataKind.String)],
            ["c"]);

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

        ExecutionContext ctx = CreateExecutionContext();
        List<Row> results = await CollectAsync(groupBy, ctx);

        Assert.Single(results);
        string[] elements = results[0]["result"].AsStringArray(ctx.Store);
        Assert.Equal(2, elements.Length);
        Assert.Equal("a", elements[0]);
        Assert.Equal("c", elements[1]);
    }

    [Fact]
    public async Task ArrayAgg_PerGroup()
    {
        MockOperator source = CreateMockOperator(CatXColumns,
            ["A", 1f],
            ["B", 10f],
            ["A", 2f],
            ["B", 20f]);

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

        ExecutionContext ctx = CreateExecutionContext();
        List<Row> results = await CollectAsync(groupBy, ctx);

        Assert.Equal(2, results.Count);

        Row groupA = results.First(row => row["cat"].AsString(ctx.Store) == "A");
        Row groupB = results.First(row => row["cat"].AsString(ctx.Store) == "B");

        ReadOnlySpan<float> elementsA = groupA["result"].AsArraySpan<float>(ctx.Store);
        Assert.Equal(2, elementsA.Length);
        Assert.Equal(1f, elementsA[0]);
        Assert.Equal(2f, elementsA[1]);

        ReadOnlySpan<float> elementsB = groupB["result"].AsArraySpan<float>(ctx.Store);
        Assert.Equal(2, elementsB.Length);
        Assert.Equal(10f, elementsB[0]);
        Assert.Equal(20f, elementsB[1]);
    }

    [Fact]
    public async Task ArrayAgg_WithOrderByAscending()
    {
        MockOperator source = CreateMockOperator(XColumns,
            [3f],
            [1f],
            [2f]);

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

        ExecutionContext ctx = CreateExecutionContext();
        List<Row> results = await CollectAsync(groupBy, ctx);

        Assert.Single(results);
        ReadOnlySpan<float> elements = results[0]["result"].AsArraySpan<float>(ctx.Store);
        Assert.Equal(1f, elements[0]);
        Assert.Equal(2f, elements[1]);
        Assert.Equal(3f, elements[2]);
    }

    [Fact]
    public async Task ArrayAgg_WithOrderByDescending()
    {
        MockOperator source = CreateMockOperator(XColumns,
            [3f],
            [1f],
            [2f]);

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

        ExecutionContext ctx = CreateExecutionContext();
        List<Row> results = await CollectAsync(groupBy, ctx);

        Assert.Single(results);
        ReadOnlySpan<float> elements = results[0]["result"].AsArraySpan<float>(ctx.Store);
        Assert.Equal(3f, elements[0]);
        Assert.Equal(2f, elements[1]);
        Assert.Equal(1f, elements[2]);
    }

    [Fact]
    public async Task ArrayAgg_WithDistinct()
    {
        MockOperator source = CreateMockOperator(XColumns,
            ["a"],
            ["b"],
            ["a"],
            ["c"],
            ["b"]);

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

        ExecutionContext ctx = CreateExecutionContext();
        List<Row> results = await CollectAsync(groupBy, ctx);

        Assert.Single(results);
        string[] elements = results[0]["result"].AsStringArray(ctx.Store);
        Assert.Equal(3, elements.Length);

        // DISTINCT should yield exactly {"a", "b", "c"} in some order
        string[] values = elements.Order().ToArray();
        Assert.Equal(["a", "b", "c"], values);
    }

    // REGISTRY

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
