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
/// Tests for statistical aggregate functions: VARIANCE, VAR_SAMP, VAR_POP,
/// STDDEV, STDDEV_SAMP, STDDEV_POP, MEDIAN, and PERCENTILE_CONT.
/// </summary>
public class StatisticalAggregateTests
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

    // ─────────────── VARIANCE / VAR_SAMP ───────────────

    [Fact]
    public async Task VarianceSample_Global()
    {
        // Values: {2, 4, 6} → mean = 4, sample variance = ((2-4)² + (4-4)² + (6-4)²) / (3-1) = 8/2 = 4.0
        MockOperator source = new(
            MakeRow(("x", DataValue.FromFloat32(2f))),
            MakeRow(("x", DataValue.FromFloat32(4f))),
            MakeRow(("x", DataValue.FromFloat32(6f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new VarianceFunction(usePopulation: false, "VARIANCE"),
                    [new ColumnReference("x")],
                    "VARIANCE(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(4.0f, results[0]["VARIANCE(x)"].AsFloat32(), 0.001f);
    }

    [Fact]
    public async Task VariancePopulation_Global()
    {
        // Values: {2, 4, 6} → mean = 4, pop variance = 8/3 ≈ 2.667
        MockOperator source = new(
            MakeRow(("x", DataValue.FromFloat32(2f))),
            MakeRow(("x", DataValue.FromFloat32(4f))),
            MakeRow(("x", DataValue.FromFloat32(6f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new VarianceFunction(usePopulation: true, "VAR_POP"),
                    [new ColumnReference("x")],
                    "VAR_POP(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(2.667f, results[0]["VAR_POP(x)"].AsFloat32(), 0.01f);
    }

    [Fact]
    public async Task VarianceSample_SingleValue_ReturnsNull()
    {
        MockOperator source = new(
            MakeRow(("x", DataValue.FromFloat32(5f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new VarianceFunction(usePopulation: false, "VAR_SAMP"),
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
        MockOperator source = new(
            MakeRow(("x", DataValue.FromFloat32(5f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new VarianceFunction(usePopulation: true, "VAR_POP"),
                    [new ColumnReference("x")],
                    "VAR_POP(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(0f, results[0]["VAR_POP(x)"].AsFloat32());
    }

    [Fact]
    public async Task Variance_AllNull_ReturnsNull()
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
                    new VarianceFunction(usePopulation: false, "VARIANCE"),
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
        // Group A: {1, 3, 5} → sample variance = 4.0
        // Group B: {10, 10} → sample variance = 0.0
        MockOperator source = new(
            MakeRow(("cat", DataValue.FromString("A")), ("x", DataValue.FromFloat32(1f))),
            MakeRow(("cat", DataValue.FromString("B")), ("x", DataValue.FromFloat32(10f))),
            MakeRow(("cat", DataValue.FromString("A")), ("x", DataValue.FromFloat32(3f))),
            MakeRow(("cat", DataValue.FromString("B")), ("x", DataValue.FromFloat32(10f))),
            MakeRow(("cat", DataValue.FromString("A")), ("x", DataValue.FromFloat32(5f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("cat")],
            aggregateColumns:
            [
                new AggregateColumn(
                    new VarianceFunction(usePopulation: false, "VARIANCE"),
                    [new ColumnReference("x")],
                    "VARIANCE(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Equal(2, results.Count);

        Row groupA = results.First(row => row["cat"].AsString() == "A");
        Row groupB = results.First(row => row["cat"].AsString() == "B");

        Assert.Equal(4.0f, groupA["VARIANCE(x)"].AsFloat32(), 0.001f);
        Assert.Equal(0.0f, groupB["VARIANCE(x)"].AsFloat32(), 0.001f);
    }

    // ─────────────── STDDEV / STDDEV_SAMP ───────────────

    [Fact]
    public async Task StdDevSample_Global()
    {
        // Values: {2, 4, 6} → sample variance = 4.0, sample stddev = 2.0
        MockOperator source = new(
            MakeRow(("x", DataValue.FromFloat32(2f))),
            MakeRow(("x", DataValue.FromFloat32(4f))),
            MakeRow(("x", DataValue.FromFloat32(6f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new StandardDeviationFunction(usePopulation: false, "STDDEV"),
                    [new ColumnReference("x")],
                    "STDDEV(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(2.0f, results[0]["STDDEV(x)"].AsFloat32(), 0.001f);
    }

    [Fact]
    public async Task StdDevPopulation_Global()
    {
        // Values: {2, 4, 6} → pop variance = 2.667, pop stddev ≈ 1.633
        MockOperator source = new(
            MakeRow(("x", DataValue.FromFloat32(2f))),
            MakeRow(("x", DataValue.FromFloat32(4f))),
            MakeRow(("x", DataValue.FromFloat32(6f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new StandardDeviationFunction(usePopulation: true, "STDDEV_POP"),
                    [new ColumnReference("x")],
                    "STDDEV_POP(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(1.633f, results[0]["STDDEV_POP(x)"].AsFloat32(), 0.01f);
    }

    [Fact]
    public async Task StdDevSample_SingleValue_ReturnsNull()
    {
        MockOperator source = new(
            MakeRow(("x", DataValue.FromFloat32(42f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new StandardDeviationFunction(usePopulation: false, "STDDEV"),
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
        MockOperator source = new(
            MakeRow(("x", DataValue.Null(DataKind.Float32))),
            MakeRow(("x", DataValue.Null(DataKind.Float32))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new StandardDeviationFunction(usePopulation: true, "STDDEV_POP"),
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
        // {1, 3, 5} → median = 3
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
                    new MedianFunction(),
                    [new ColumnReference("x")],
                    "MEDIAN(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(3f, results[0]["MEDIAN(x)"].AsFloat32());
    }

    [Fact]
    public async Task Median_EvenCount()
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
                    new MedianFunction(),
                    [new ColumnReference("x")],
                    "MEDIAN(x)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(4f, results[0]["MEDIAN(x)"].AsFloat32());
    }

    [Fact]
    public async Task Median_SingleValue()
    {
        MockOperator source = new(
            MakeRow(("x", DataValue.FromFloat32(42f))));

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
        Assert.Equal(42f, results[0]["MEDIAN(x)"].AsFloat32());
    }

    [Fact]
    public async Task Median_AllNull_ReturnsNull()
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
        // Group A: {10, 20, 30} → median = 20
        // Group B: {1, 2, 3, 4} → median = 2.5
        MockOperator source = new(
            MakeRow(("cat", DataValue.FromString("A")), ("x", DataValue.FromFloat32(30f))),
            MakeRow(("cat", DataValue.FromString("B")), ("x", DataValue.FromFloat32(4f))),
            MakeRow(("cat", DataValue.FromString("A")), ("x", DataValue.FromFloat32(10f))),
            MakeRow(("cat", DataValue.FromString("B")), ("x", DataValue.FromFloat32(1f))),
            MakeRow(("cat", DataValue.FromString("A")), ("x", DataValue.FromFloat32(20f))),
            MakeRow(("cat", DataValue.FromString("B")), ("x", DataValue.FromFloat32(2f))),
            MakeRow(("cat", DataValue.FromString("B")), ("x", DataValue.FromFloat32(3f))));

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

        Row groupA = results.First(row => row["cat"].AsString() == "A");
        Row groupB = results.First(row => row["cat"].AsString() == "B");

        Assert.Equal(20f, groupA["MEDIAN(x)"].AsFloat32());
        Assert.Equal(2.5f, groupB["MEDIAN(x)"].AsFloat32());
    }

    [Fact]
    public async Task Median_SkipsNullValues()
    {
        // Non-null values: {1, 5, 9} → median = 5
        MockOperator source = new(
            MakeRow(("x", DataValue.FromFloat32(1f))),
            MakeRow(("x", DataValue.Null(DataKind.Float32))),
            MakeRow(("x", DataValue.FromFloat32(9f))),
            MakeRow(("x", DataValue.FromFloat32(5f))));

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
        Assert.Equal(5f, results[0]["MEDIAN(x)"].AsFloat32());
    }

    // ─────────────── PERCENTILE_CONT ───────────────

    [Fact]
    public async Task PercentileCont_Median()
    {
        // P50 of {1, 2, 3, 4, 5} → 3 (same as median for odd count)
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
                    new PercentileContinuousFunction(),
                    [new ColumnReference("x"), new ColumnReference("p")],
                    "PERCENTILE_CONT(x, 0.5)"),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Single(results);
        Assert.Equal(3f, results[0]["PERCENTILE_CONT(x, 0.5)"].AsFloat32(), 0.001f);
    }

    [Fact]
    public async Task PercentileCont_P0_ReturnsMinimum()
    {
        // P0 of {10, 20, 30} → 10
        MockOperator source = new(
            MakeRow(("x", DataValue.FromFloat32(30f)), ("p", DataValue.FromFloat32(0f))),
            MakeRow(("x", DataValue.FromFloat32(10f)), ("p", DataValue.FromFloat32(0f))),
            MakeRow(("x", DataValue.FromFloat32(20f)), ("p", DataValue.FromFloat32(0f))));

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
        Assert.Equal(10f, results[0]["P0"].AsFloat32());
    }

    [Fact]
    public async Task PercentileCont_P100_ReturnsMaximum()
    {
        // P100 of {10, 20, 30} → 30
        MockOperator source = new(
            MakeRow(("x", DataValue.FromFloat32(30f)), ("p", DataValue.FromFloat32(1f))),
            MakeRow(("x", DataValue.FromFloat32(10f)), ("p", DataValue.FromFloat32(1f))),
            MakeRow(("x", DataValue.FromFloat32(20f)), ("p", DataValue.FromFloat32(1f))));

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
        Assert.Equal(30f, results[0]["P100"].AsFloat32());
    }

    [Fact]
    public async Task PercentileCont_Interpolation()
    {
        // P30 of {1, 2, 3, 4} → row = 0.3 * 3 = 0.9, lower=0(val=1), upper=1(val=2)
        // interpolated = 1 + (2 - 1) * 0.9 = 1.9
        MockOperator source = new(
            MakeRow(("x", DataValue.FromFloat32(4f)), ("p", DataValue.FromFloat32(0.3f))),
            MakeRow(("x", DataValue.FromFloat32(2f)), ("p", DataValue.FromFloat32(0.3f))),
            MakeRow(("x", DataValue.FromFloat32(1f)), ("p", DataValue.FromFloat32(0.3f))),
            MakeRow(("x", DataValue.FromFloat32(3f)), ("p", DataValue.FromFloat32(0.3f))));

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
        Assert.Equal(1.9f, results[0]["P30"].AsFloat32(), 0.001f);
    }

    [Fact]
    public async Task PercentileCont_Quartiles()
    {
        // {1, 2, 3, 4, 5} → P25: row=1.0 → val=2, P75: row=3.0 → val=4
        MockOperator source = new(
            MakeRow(("x", DataValue.FromFloat32(1f)), ("p25", DataValue.FromFloat32(0.25f)), ("p75", DataValue.FromFloat32(0.75f))),
            MakeRow(("x", DataValue.FromFloat32(2f)), ("p25", DataValue.FromFloat32(0.25f)), ("p75", DataValue.FromFloat32(0.75f))),
            MakeRow(("x", DataValue.FromFloat32(3f)), ("p25", DataValue.FromFloat32(0.25f)), ("p75", DataValue.FromFloat32(0.75f))),
            MakeRow(("x", DataValue.FromFloat32(4f)), ("p25", DataValue.FromFloat32(0.25f)), ("p75", DataValue.FromFloat32(0.75f))),
            MakeRow(("x", DataValue.FromFloat32(5f)), ("p25", DataValue.FromFloat32(0.25f)), ("p75", DataValue.FromFloat32(0.75f))));

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
        Assert.Equal(2f, results[0]["Q1"].AsFloat32(), 0.001f);
        Assert.Equal(4f, results[0]["Q3"].AsFloat32(), 0.001f);
    }

    [Fact]
    public async Task PercentileCont_AllNull_ReturnsNull()
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
            accumulator.Accumulate([DataValue.FromFloat32(1f), DataValue.FromFloat32(1.5f)]));
    }

    [Fact]
    public void PercentileCont_NegativeFraction_Throws()
    {
        PercentileContinuousFunction function = new();
        IAggregateAccumulator accumulator = function.CreateAccumulator();

        Assert.Throws<ArgumentException>(() =>
            accumulator.Accumulate([DataValue.FromFloat32(1f), DataValue.FromFloat32(-0.1f)]));
    }

    // ─────────────── Argument validation ───────────────

    [Fact]
    public void Variance_WrongArgCount_Throws()
    {
        VarianceFunction function = new(usePopulation: false, "VARIANCE");

        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([]));

        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Float32, DataKind.Float32]));
    }

    [Fact]
    public void Variance_NonNumericArg_Throws()
    {
        VarianceFunction function = new(usePopulation: false, "VARIANCE");

        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.String]));
    }

    [Fact]
    public void StdDev_WrongArgCount_Throws()
    {
        StandardDeviationFunction function = new(usePopulation: false, "STDDEV");

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
