using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Functions.Aggregates;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for SELECT DISTINCT (<see cref="DistinctOperator"/>),
/// aggregate DISTINCT (<see cref="DistinctAccumulatorDecorator"/>),
/// and semantic validation of DISTINCT usage.
/// </summary>
public class DistinctTests
{
    private static ExecutionContext CreateContext(long? memoryBudgetBytes = null)
    {
        return new ExecutionContext(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            new TableCatalog(),
            new LocalBufferPool(),
            memoryBudgetBytes: memoryBudgetBytes);
    }

    private static TableCatalog CreateCatalogWithTable(string tableName)
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.Register(new TableDescriptor("csv", tableName, "dummy.csv", new Dictionary<string, string>()));
        return catalog;
    }

    private static Row MakeRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = columns.Select(c => c.Name).ToArray();
        DataValue[] values = columns.Select(c => c.Value).ToArray();
        return new Row(names, values);
    }

    private static async Task<List<Row>> CollectAsync(
        IQueryOperator op, ExecutionContext? context = null)
    {
        context ??= CreateContext();
        List<Row> rows = [];
        await foreach (RowBatch batch in op.ExecuteAsync(context))
        {
            for (int index = 0; index < batch.Count; index++)
            {
                rows.Add(batch[index]);
            }
        }

        return rows;
    }

    // ─────────────── DistinctOperator ───────────────

    [Fact]
    public async Task DistinctOperator_EmptySource_YieldsNoRows()
    {
        MockOperator source = new();
        DistinctOperator distinct = new(source);

        List<Row> results = await CollectAsync(distinct);

        Assert.Empty(results);
    }

    [Fact]
    public async Task DistinctOperator_AllUnique_YieldsAll()
    {
        MockOperator source = new(
            MakeRow(("x", DataValue.FromFloat32(1f))),
            MakeRow(("x", DataValue.FromFloat32(2f))),
            MakeRow(("x", DataValue.FromFloat32(3f))));

        DistinctOperator distinct = new(source);

        List<Row> results = await CollectAsync(distinct);

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task DistinctOperator_AllSame_YieldsOne()
    {
        MockOperator source = new(
            MakeRow(("x", DataValue.FromFloat32(42f))),
            MakeRow(("x", DataValue.FromFloat32(42f))),
            MakeRow(("x", DataValue.FromFloat32(42f))));

        DistinctOperator distinct = new(source);

        List<Row> results = await CollectAsync(distinct);

        Assert.Single(results);
        Assert.Equal(42f, results[0]["x"].AsFloat32());
    }

    [Fact]
    public async Task DistinctOperator_DuplicatesRemoved()
    {
        MockOperator source = new(
            MakeRow(("x", DataValue.FromFloat32(1f))),
            MakeRow(("x", DataValue.FromFloat32(2f))),
            MakeRow(("x", DataValue.FromFloat32(1f))),
            MakeRow(("x", DataValue.FromFloat32(3f))),
            MakeRow(("x", DataValue.FromFloat32(2f))));

        DistinctOperator distinct = new(source);

        List<Row> results = await CollectAsync(distinct);

        Assert.Equal(3, results.Count);
        float[] values = results.Select(r => r["x"].AsFloat32()).OrderBy(v => v).ToArray();
        Assert.Equal([1f, 2f, 3f], values);
    }

    [Fact]
    public async Task DistinctOperator_MultiColumn_DeduplicatesOnAllColumns()
    {
        MockOperator source = new(
            MakeRow(("a", DataValue.FromFloat32(1f)), ("b", DataValue.FromFloat32(10f))),
            MakeRow(("a", DataValue.FromFloat32(1f)), ("b", DataValue.FromFloat32(20f))),
            MakeRow(("a", DataValue.FromFloat32(1f)), ("b", DataValue.FromFloat32(10f))),
            MakeRow(("a", DataValue.FromFloat32(2f)), ("b", DataValue.FromFloat32(10f))));

        DistinctOperator distinct = new(source);

        List<Row> results = await CollectAsync(distinct);

        // (1,10), (1,20), (2,10) — 3 unique combos.
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task DistinctOperator_NullValues_TreatedAsEqual()
    {
        MockOperator source = new(
            MakeRow(("x", DataValue.Null(DataKind.Float32))),
            MakeRow(("x", DataValue.FromFloat32(1f))),
            MakeRow(("x", DataValue.Null(DataKind.Float32))));

        DistinctOperator distinct = new(source);

        List<Row> results = await CollectAsync(distinct);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task DistinctOperator_StringValues()
    {
        MockOperator source = new(
            MakeRow(("name", DataValue.FromString("alice"))),
            MakeRow(("name", DataValue.FromString("bob"))),
            MakeRow(("name", DataValue.FromString("alice"))),
            MakeRow(("name", DataValue.FromString("charlie"))),
            MakeRow(("name", DataValue.FromString("bob"))));

        DistinctOperator distinct = new(source);

        List<Row> results = await CollectAsync(distinct);

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task DistinctOperator_SpillToDisk_ProducesCorrectResults()
    {
        // Create enough rows to exceed a tiny memory budget, forcing spill.
        List<Row> sourceRows = [];
        for (int index = 0; index < 200; index++)
        {
            // Create values 0-99 twice so half are duplicates.
            sourceRows.Add(MakeRow(("x", DataValue.FromFloat32(index % 100))));
        }

        MockOperator source = new(sourceRows.ToArray());
        DistinctOperator distinct = new(source);

        // Very small budget to force spill behavior.
        ExecutionContext context = CreateContext(memoryBudgetBytes: 512);
        List<Row> results = await CollectAsync(distinct, context);

        distinct.Dispose();

        Assert.Equal(100, results.Count);
        HashSet<float> uniqueValues = results.Select(r => r["x"].AsFloat32()).ToHashSet();
        Assert.Equal(100, uniqueValues.Count);
    }

    // ─────────────── DistinctAccumulatorDecorator ───────────────

    [Fact]
    public void DistinctAccumulator_CountDistinct_IgnoresDuplicates()
    {
        CountFunction countFunction = new();
        IAggregateAccumulator accumulator = countFunction.CreateAccumulator();
        DistinctAccumulatorDecorator decorator = new(accumulator, argumentCount: 1);

        DataValue[] values = [DataValue.FromFloat32(1f), DataValue.FromFloat32(2f),
            DataValue.FromFloat32(1f), DataValue.FromFloat32(3f), DataValue.FromFloat32(2f)];

        foreach (DataValue value in values)
        {
            decorator.Accumulate([value]);
        }

        // 3 distinct values: 1, 2, 3.
        Assert.Equal(3f, decorator.Result.AsFloat32());
    }

    [Fact]
    public void DistinctAccumulator_SumDistinct_SumsOnlyUniqueValues()
    {
        SumFunction sumFunction = new();
        IAggregateAccumulator accumulator = sumFunction.CreateAccumulator();
        DistinctAccumulatorDecorator decorator = new(accumulator, argumentCount: 1);

        DataValue[] values = [DataValue.FromFloat32(10f), DataValue.FromFloat32(20f),
            DataValue.FromFloat32(10f), DataValue.FromFloat32(30f)];

        foreach (DataValue value in values)
        {
            decorator.Accumulate([value]);
        }

        // 10 + 20 + 30 = 60 (not 70).
        Assert.Equal(60f, decorator.Result.AsFloat32());
    }

    [Fact]
    public void DistinctAccumulator_AllSameValues_AccumulatesOnce()
    {
        CountFunction countFunction = new();
        IAggregateAccumulator accumulator = countFunction.CreateAccumulator();
        DistinctAccumulatorDecorator decorator = new(accumulator, argumentCount: 1);

        for (int index = 0; index < 5; index++)
        {
            decorator.Accumulate([DataValue.FromFloat32(42f)]);
        }

        Assert.Equal(1f, decorator.Result.AsFloat32());
    }

    [Fact]
    public void DistinctAccumulator_NullValues_TreatedAsEqual()
    {
        CountFunction countFunction = new();
        IAggregateAccumulator accumulator = countFunction.CreateAccumulator();
        DistinctAccumulatorDecorator decorator = new(accumulator, argumentCount: 1);

        decorator.Accumulate([DataValue.Null(DataKind.Float32)]);
        decorator.Accumulate([DataValue.FromFloat32(1f)]);
        decorator.Accumulate([DataValue.Null(DataKind.Float32)]);

        // The distinct decorator treats NULLs as equal (2 distinct values seen),
        // but COUNT's inner accumulator skips NULL values. Only 1f is counted.
        Assert.Equal(1f, decorator.Result.AsFloat32());
    }

    [Fact]
    public void DistinctAccumulator_EmptyInput_ReturnsInnerDefault()
    {
        CountFunction countFunction = new();
        IAggregateAccumulator accumulator = countFunction.CreateAccumulator();
        DistinctAccumulatorDecorator decorator = new(accumulator, argumentCount: 1);

        Assert.Equal(0f, decorator.Result.AsFloat32());
    }

    // ─────────────── GroupByOperator with DISTINCT aggregates ───────────────

    [Fact]
    public async Task GroupBy_CountDistinct_DeduplicatesPerGroup()
    {
        MockOperator source = new(
            MakeRow(("category", DataValue.FromString("A")), ("item", DataValue.FromFloat32(1f))),
            MakeRow(("category", DataValue.FromString("A")), ("item", DataValue.FromFloat32(2f))),
            MakeRow(("category", DataValue.FromString("A")), ("item", DataValue.FromFloat32(1f))),
            MakeRow(("category", DataValue.FromString("B")), ("item", DataValue.FromFloat32(10f))),
            MakeRow(("category", DataValue.FromString("B")), ("item", DataValue.FromFloat32(10f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("category")],
            aggregateColumns:
            [
                new AggregateColumn(
                    new CountFunction(),
                    [new ColumnReference("item")],
                    "COUNT(DISTINCT item)",
                    Distinct: true),
            ]);

        List<Row> results = await CollectAsync(groupBy);

        Assert.Equal(2, results.Count);

        Row groupA = results.First(r => r["category"].AsString() == "A");
        Row groupB = results.First(r => r["category"].AsString() == "B");

        Assert.Equal(2f, groupA["COUNT(DISTINCT item)"].AsFloat32());
        Assert.Equal(1f, groupB["COUNT(DISTINCT item)"].AsFloat32());
    }

    // ─────────────── Validation ───────────────

    [Fact]
    public void Validation_CountDistinctStar_Throws()
    {
        TableCatalog catalog = CreateCatalogWithTable("t");
        FunctionRegistry functions = FunctionRegistry.CreateDefault();
        QueryPlanner planner = new(catalog, functions);

        SelectStatement statement = new(
            Columns: [new SelectColumn(
                new FunctionCallExpression("COUNT", [new LiteralExpression("*")], Distinct: true))],
            From: new FromClause(new TableReference("t")));

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => planner.Plan(statement));
        Assert.Contains("COUNT(DISTINCT *)", exception.Message);
    }

    [Fact]
    public void Validation_DistinctOrderByNonSelectedColumn_Throws()
    {
        TableCatalog catalog = CreateCatalogWithTable("t");
        FunctionRegistry functions = FunctionRegistry.CreateDefault();
        QueryPlanner planner = new(catalog, functions);

        SelectStatement statement = new(
            Columns: [new SelectColumn(new ColumnReference("name"))],
            From: new FromClause(new TableReference("t")),
            Distinct: true,
            OrderBy: new OrderByClause(
                [new OrderByItem(new ColumnReference("age"), SortDirection.Ascending)]));

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => planner.Plan(statement));
        Assert.Contains("ORDER BY", exception.Message);
        Assert.Contains("SELECT list", exception.Message);
    }

    [Fact]
    public void Validation_DistinctOrderBySelectedColumn_Succeeds()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new DatumIngest.Catalog.Providers.CsvTableProvider());
        catalog.Register(new TableDescriptor("csv", "t", "dummy.csv", new Dictionary<string, string>()));

        FunctionRegistry functions = FunctionRegistry.CreateDefault();
        QueryPlanner planner = new(catalog, functions);

        SelectStatement statement = new(
            Columns: [new SelectColumn(new ColumnReference("name"))],
            From: new FromClause(new TableReference("t")),
            Distinct: true,
            OrderBy: new OrderByClause(
                [new OrderByItem(new ColumnReference("name"), SortDirection.Ascending)]));

        // Should not throw.
        IQueryOperator plan = planner.Plan(statement);
        Assert.NotNull(plan);
    }

    [Fact]
    public void Validation_WindowDistinct_Throws()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new DatumIngest.Catalog.Providers.CsvTableProvider());
        catalog.Register(new TableDescriptor("csv", "t", "dummy.csv", new Dictionary<string, string>()));

        FunctionRegistry functions = FunctionRegistry.CreateDefault();
        QueryPlanner planner = new(catalog, functions);

        SelectStatement statement = new(
            Columns: [new SelectColumn(
                new WindowFunctionCallExpression(
                    "COUNT",
                    [new ColumnReference("x")],
                    Distinct: true,
                    Span: default,
                    Window: new WindowSpecification(null, null, null)))],
            From: new FromClause(new TableReference("t")));

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => planner.Plan(statement));
        Assert.Contains("DISTINCT is not supported in window functions", exception.Message);
    }
}
