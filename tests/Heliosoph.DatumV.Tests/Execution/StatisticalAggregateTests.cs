using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Execution.Operators;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Aggregates;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Tests.Execution;

/// <summary>
/// Tests for statistical aggregate functions: VARIANCE, VAR_SAMP, VAR_POP,
/// STDDEV, STDDEV_SAMP, STDDEV_POP, MEDIAN, and PERCENTILE_CONT.
/// </summary>
public class StatisticalAggregateTests : ServiceTestBase
{
    private static readonly string[] XColumns = ["x"];
    private static readonly string[] CatXColumns = ["cat", "x"];
    private static readonly string[] XpColumns = ["x", "p"];

    private readonly InvocationFrame _testFrame;

    public StatisticalAggregateTests()
    {
        _testFrame = InvocationFrame.Symmetric(CreateArena());
    }

    private async Task<List<Row>> CollectAsync(QueryOperator op, ExecutionContext? context = null)
    {
        context ??= CreateExecutionContext();
        return await op.CollectRowsAsync(context);
    }

    // ─────────────── VARIANCE / VAR_SAMP ───────────────

    [Fact]
    public async Task VarianceSample_Global()
    {
        // Values: {2, 4, 6} -> mean = 4, sample variance = 4.0
        MockOperator source = CreateMockOperator(XColumns,
            [2f],
            [4f],
            [6f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new VarianceFunction(),
                    [new ColumnReference("x")],
                    "VARIANCE(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(4.0, results[0]["VARIANCE(x)"].AsFloat64(), 0.001);
    }

    [Fact]
    public async Task VariancePopulation_Global()
    {
        // Values: {2, 4, 6} -> mean = 4, pop variance = 8/3 ~ 2.667
        MockOperator source = CreateMockOperator(XColumns,
            [2f],
            [4f],
            [6f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new VariancePopulationFunction(),
                    [new ColumnReference("x")],
                    "VAR_POP(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(2.667, results[0]["VAR_POP(x)"].AsFloat64(), 0.01);
    }

    [Fact]
    public async Task VarianceSample_SingleValue_ReturnsNull()
    {
        MockOperator source = CreateMockOperator(XColumns,
            [5f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new VarianceFunction(),
                    [new ColumnReference("x")],
                    "VAR_SAMP(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.True(results[0]["VAR_SAMP(x)"].IsNull);
    }

    [Fact]
    public async Task VariancePopulation_SingleValue_ReturnsZero()
    {
        MockOperator source = CreateMockOperator(XColumns,
            [5f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new VariancePopulationFunction(),
                    [new ColumnReference("x")],
                    "VAR_POP(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(0.0, results[0]["VAR_POP(x)"].AsFloat64());
    }

    [Fact]
    public async Task Variance_AllNull_ReturnsNull()
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
                    new VarianceFunction(),
                    [new ColumnReference("x")],
                    "VARIANCE(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.True(results[0]["VARIANCE(x)"].IsNull);
    }

    [Fact]
    public async Task Variance_PerGroup()
    {
        // Group A: {1, 3, 5} -> sample variance = 4.0
        // Group B: {10, 10}  -> sample variance = 0.0
        MockOperator source = CreateMockOperator(CatXColumns,
            ["A", 1f],
            ["B", 10f],
            ["A", 3f],
            ["B", 10f],
            ["A", 5f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("cat")],
            aggregateColumns:
            [
                new AggregateColumn(
                    new VarianceFunction(),
                    [new ColumnReference("x")],
                    "VARIANCE(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Equal(2, results.Count);

        Row groupA = results.First(row => row["cat"].AsString(_testFrame.Target) == "A");
        Row groupB = results.First(row => row["cat"].AsString(_testFrame.Target) == "B");

        Assert.Equal(4.0, groupA["VARIANCE(x)"].AsFloat64(), 0.001);
        Assert.Equal(0.0, groupB["VARIANCE(x)"].AsFloat64(), 0.001);
    }

    // ─────────────── STDDEV / STDDEV_SAMP ───────────────

    [Fact]
    public async Task StdDevSample_Global()
    {
        // Values: {2, 4, 6} -> sample variance = 4.0, sample stddev = 2.0
        MockOperator source = CreateMockOperator(XColumns,
            [2f],
            [4f],
            [6f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new StandardDeviationFunction(),
                    [new ColumnReference("x")],
                    "STDDEV(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(2.0, results[0]["STDDEV(x)"].AsFloat64(), 0.001);
    }

    [Fact]
    public async Task StdDevPopulation_Global()
    {
        // Values: {2, 4, 6} -> pop variance = 2.667, pop stddev ~ 1.633
        MockOperator source = CreateMockOperator(XColumns,
            [2f],
            [4f],
            [6f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new StandardDeviationPopulationFunction(),
                    [new ColumnReference("x")],
                    "STDDEV_POP(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(1.633, results[0]["STDDEV_POP(x)"].AsFloat64(), 0.01);
    }

    [Fact]
    public async Task StdDevSample_SingleValue_ReturnsNull()
    {
        MockOperator source = CreateMockOperator(XColumns,
            [42f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new StandardDeviationFunction(),
                    [new ColumnReference("x")],
                    "STDDEV(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.True(results[0]["STDDEV(x)"].IsNull);
    }

    [Fact]
    public async Task StdDev_AllNull_ReturnsNull()
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
                    new StandardDeviationPopulationFunction(),
                    [new ColumnReference("x")],
                    "STDDEV_POP(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.True(results[0]["STDDEV_POP(x)"].IsNull);
    }

    // ─────────────── MEDIAN ───────────────

    [Fact]
    public async Task Median_OddCount()
    {
        // {1, 3, 5} -> median = 3
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
                    new MedianFunction(),
                    [new ColumnReference("x")],
                    "MEDIAN(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(3.0, results[0]["MEDIAN(x)"].AsFloat64());
    }

    [Fact]
    public async Task Median_EvenCount()
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
                    new MedianFunction(),
                    [new ColumnReference("x")],
                    "MEDIAN(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(4.0, results[0]["MEDIAN(x)"].AsFloat64());
    }

    [Fact]
    public async Task Median_SingleValue()
    {
        MockOperator source = CreateMockOperator(XColumns,
            [42f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new MedianFunction(),
                    [new ColumnReference("x")],
                    "MEDIAN(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(42.0, results[0]["MEDIAN(x)"].AsFloat64());
    }

    [Fact]
    public async Task Median_AllNull_ReturnsNull()
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
                    new MedianFunction(),
                    [new ColumnReference("x")],
                    "MEDIAN(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.True(results[0]["MEDIAN(x)"].IsNull);
    }

    [Fact]
    public async Task Median_PerGroup()
    {
        // Group A: {10, 20, 30}  -> median = 20
        // Group B: {1, 2, 3, 4}  -> median = 2.5
        MockOperator source = CreateMockOperator(CatXColumns,
            ["A", 30f],
            ["B", 4f],
            ["A", 10f],
            ["B", 1f],
            ["A", 20f],
            ["B", 2f],
            ["B", 3f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("cat")],
            aggregateColumns:
            [
                new AggregateColumn(
                    new MedianFunction(),
                    [new ColumnReference("x")],
                    "MEDIAN(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Equal(2, results.Count);

        Row groupA = results.First(row => row["cat"].AsString(_testFrame.Target) == "A");
        Row groupB = results.First(row => row["cat"].AsString(_testFrame.Target) == "B");

        Assert.Equal(20.0, groupA["MEDIAN(x)"].AsFloat64());
        Assert.Equal(2.5, groupB["MEDIAN(x)"].AsFloat64());
    }

    [Fact]
    public async Task Median_SkipsNullValues()
    {
        // Non-null values: {1, 5, 9} -> median = 5
        MockOperator source = CreateMockOperator(XColumns,
            [1f],
            [DataValue.Null(DataKind.Float32)],
            [9f],
            [5f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new MedianFunction(),
                    [new ColumnReference("x")],
                    "MEDIAN(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(5.0, results[0]["MEDIAN(x)"].AsFloat64());
    }

    // ─────────────── PERCENTILE_CONT ───────────────

    [Fact]
    public async Task PercentileCont_Median()
    {
        // P50 of {1, 2, 3, 4, 5} -> 3 (same as median for odd count)
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
                    new PercentileContinuousFunction(),
                    [new ColumnReference("x"), new ColumnReference("p")],
                    "PERCENTILE_CONT(x, 0.5)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(3.0, results[0]["PERCENTILE_CONT(x, 0.5)"].AsFloat64(), 0.001);
    }

    [Fact]
    public async Task PercentileCont_P0_ReturnsMinimum()
    {
        // P0 of {10, 20, 30} -> 10
        MockOperator source = CreateMockOperator(XpColumns,
            [30f, 0f],
            [10f, 0f],
            [20f, 0f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new PercentileContinuousFunction(),
                    [new ColumnReference("x"), new ColumnReference("p")],
                    "P0"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(10.0, results[0]["P0"].AsFloat64());
    }

    [Fact]
    public async Task PercentileCont_P100_ReturnsMaximum()
    {
        // P100 of {10, 20, 30} -> 30
        MockOperator source = CreateMockOperator(XpColumns,
            [30f, 1f],
            [10f, 1f],
            [20f, 1f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new PercentileContinuousFunction(),
                    [new ColumnReference("x"), new ColumnReference("p")],
                    "P100"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(30.0, results[0]["P100"].AsFloat64());
    }

    [Fact]
    public async Task PercentileCont_Interpolation()
    {
        // P30 of {1, 2, 3, 4} -> row = 0.3 * 3 = 0.9, lower=0(val=1), upper=1(val=2)
        // interpolated = 1 + (2 - 1) * 0.9 = 1.9
        MockOperator source = CreateMockOperator(XpColumns,
            [4f, 0.3f],
            [2f, 0.3f],
            [1f, 0.3f],
            [3f, 0.3f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new PercentileContinuousFunction(),
                    [new ColumnReference("x"), new ColumnReference("p")],
                    "P30"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(1.9, results[0]["P30"].AsFloat64(), 0.001);
    }

    [Fact]
    public async Task PercentileCont_Quartiles()
    {
        // {1, 2, 3, 4, 5} -> P25: row=1.0 -> val=2, P75: row=3.0 -> val=4
        MockOperator source = CreateMockOperator(["x", "p25", "p75"],
            [1f, 0.25f, 0.75f],
            [2f, 0.25f, 0.75f],
            [3f, 0.25f, 0.75f],
            [4f, 0.25f, 0.75f],
            [5f, 0.25f, 0.75f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new PercentileContinuousFunction(),
                    [new ColumnReference("x"), new ColumnReference("p25")],
                    "Q1"),
                new AggregateColumn(
                    new PercentileContinuousFunction(),
                    [new ColumnReference("x"), new ColumnReference("p75")],
                    "Q3"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(2.0, results[0]["Q1"].AsFloat64(), 0.001);
        Assert.Equal(4.0, results[0]["Q3"].AsFloat64(), 0.001);
    }

    [Fact]
    public async Task PercentileCont_AllNull_ReturnsNull()
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
                    new PercentileContinuousFunction(),
                    [new ColumnReference("x"), new ColumnReference("p")],
                    "P50"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.True(results[0]["P50"].IsNull);
    }

    [Fact]
    public void PercentileCont_InvalidFraction_Throws()
    {
        PercentileContinuousFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        Assert.Throws<ArgumentException>(() =>
            accumulator.Accumulate([DataValue.FromFloat32(1f), DataValue.FromFloat32(1.5f)], in _testFrame));
    }

    [Fact]
    public void PercentileCont_NegativeFraction_Throws()
    {
        PercentileContinuousFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        Assert.Throws<ArgumentException>(() =>
            accumulator.Accumulate([DataValue.FromFloat32(1f), DataValue.FromFloat32(-0.1f)], in _testFrame));
    }

    // ─────────────── Argument validation ───────────────

    [Fact]
    public void Variance_WrongArgCount_Throws()
    {
        VarianceFunction function = new();

        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([]));

        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Float32, DataKind.Float32]));
    }

    [Fact]
    public void Variance_NonNumericArg_Throws()
    {
        VarianceFunction function = new();

        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.String]));
    }

    [Fact]
    public void StdDev_WrongArgCount_Throws()
    {
        StandardDeviationFunction function = new();

        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([]));
    }

    [Fact]
    public void Median_WrongArgCount_Throws()
    {
        MedianFunction function = new();

        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([]));

        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Float32, DataKind.Float32]));
    }

    [Fact]
    public void Median_NonNumericArg_Throws()
    {
        MedianFunction function = new();

        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.String]));
    }

    [Fact]
    public void PercentileCont_WrongArgCount_Throws()
    {
        PercentileContinuousFunction function = new();

        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Float32]));

        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Float32, DataKind.Float32, DataKind.Float32]));
    }

    [Fact]
    public void PercentileCont_NonNumericFirstArg_Throws()
    {
        PercentileContinuousFunction function = new();

        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.String, DataKind.Float32]));
    }

    // ─────────────── Registry integration ───────────────

    [Fact]
    public void FunctionRegistry_ContainsAllStatisticalAggregates()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();

        Assert.NotNull(registry.TryGetAggregate("VARIANCE"));
        Assert.NotNull(registry.TryGetAggregate("VAR_SAMP"));
        Assert.NotNull(registry.TryGetAggregate("VAR_POP"));
        Assert.NotNull(registry.TryGetAggregate("STDDEV"));
        Assert.NotNull(registry.TryGetAggregate("STDDEV_SAMP"));
        Assert.NotNull(registry.TryGetAggregate("STDDEV_POP"));
        Assert.NotNull(registry.TryGetAggregate("MEDIAN"));
        Assert.NotNull(registry.TryGetAggregate("PERCENTILE_CONT"));
    }
}
