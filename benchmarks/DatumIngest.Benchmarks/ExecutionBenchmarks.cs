using BenchmarkDotNet.Attributes;
using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.DatumFile.V2;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Benchmarks;

/// <summary>
/// Benchmarks for full query execution including scan, filter, project, and join.
/// </summary>
/// <remarks>
/// Each benchmark runs twice — once against an <see cref="InMemoryTableProvider"/>
/// (operator-isolation, no I/O) and once against a <see cref="DatumFileTableProviderV2"/>
/// reading a real <c>.datum</c> file (end-to-end, comparable to other engines).
/// </remarks>
[MemoryDiagnoser]
public class ExecutionBenchmarks
{
    private static readonly ColumnDescriptorV2[] DataColumns =
    [
        new("id", DataKind.Float32, EncoderKind.FixedWidth, IsNullable: false),
        new("name", DataKind.String, EncoderKind.VariableSlot, IsNullable: false),
        new("value", DataKind.Float32, EncoderKind.FixedWidth, IsNullable: false),
        new("category", DataKind.String, EncoderKind.VariableSlot, IsNullable: false),
        new("score", DataKind.Float32, EncoderKind.FixedWidth, IsNullable: false),
    ];

    private static readonly ColumnDescriptorV2[] LookupColumns =
    [
        new("lookup_id", DataKind.Float32, EncoderKind.FixedWidth, IsNullable: false),
        new("description", DataKind.String, EncoderKind.VariableSlot, IsNullable: false),
        new("weight", DataKind.Float32, EncoderKind.FixedWidth, IsNullable: false),
    ];

    [Params(ProviderKind.InMemory, ProviderKind.DatumFile)]
    public ProviderKind Provider;

    private Pool _pool = null!;
    private Row[] _dataRows = null!;
    private Row[] _lookupRows = null!;
    private Row[] _dataSecondRows = null!;

    // Populated only when Provider == ProviderKind.DatumFile.
    private string? _tempDir;
    private string? _dataFilePath;
    private string? _lookupFilePath;
    private string? _dataSecondFilePath;

    [GlobalSetup]
    public void Setup()
    {
        _pool = new Pool(new PoolBacking());
        _dataRows = SyntheticDataGenerator.GenerateRows(10_000);
        _lookupRows = SyntheticDataGenerator.GenerateLookupRows(1_000);
        _dataSecondRows = SyntheticDataGenerator.GenerateRows(10_000, seed: 99);

        if (Provider == ProviderKind.DatumFile)
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"datum_bench_exec_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
            _dataFilePath = DatumFileBenchHelper.WriteRowsToDatumFile(
                _pool, Path.Combine(_tempDir, "data.datum"), _dataRows, DataColumns);
            _lookupFilePath = DatumFileBenchHelper.WriteRowsToDatumFile(
                _pool, Path.Combine(_tempDir, "lookup.datum"), _lookupRows, LookupColumns);
            _dataSecondFilePath = DatumFileBenchHelper.WriteRowsToDatumFile(
                _pool, Path.Combine(_tempDir, "data_b.datum"), _dataSecondRows, DataColumns);
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (_tempDir is not null && Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
        }
    }

    private TableCatalog BuildCatalog(bool withLookup = false, bool withDataSecond = false)
    {
        TableCatalog catalog = new(_pool);
        if (Provider == ProviderKind.InMemory)
        {
            catalog.Add(new InMemoryTableProvider(_pool, "data", _dataRows));
            if (withLookup) catalog.Add(new InMemoryTableProvider(_pool, "lookup", _lookupRows));
            if (withDataSecond) catalog.Add(new InMemoryTableProvider(_pool, "data_b", _dataSecondRows));
        }
        else
        {
            catalog.Add(new TableDescriptor("data", _dataFilePath!));
            if (withLookup) catalog.Add(new TableDescriptor("lookup", _lookupFilePath!));
            if (withDataSecond) catalog.Add(new TableDescriptor("data_b", _dataSecondFilePath!));
        }
        return catalog;
    }

    private static FunctionRegistry BuildFunctions()
    {
        return FunctionRegistry.CreateDefault();
    }

    [Benchmark(Description = "SELECT * FROM data (10K)")]
    public async Task SelectAll10K()
    {
        TableCatalog catalog = BuildCatalog();
        FunctionRegistry functions = BuildFunctions();
        QueryExpression query = SqlParser.Parse("SELECT * FROM data");
        QueryPlanner planner = new(catalog, functions);
        QueryOperator root = planner.Plan(query);
        ExecutionContext context = catalog.CreateExecutionContext();

        await foreach (RowBatch batch in root.ExecuteAsync(context))
        {
            context.ReturnRowBatch(batch);
        }
    }

    [Benchmark(Description = "SELECT with WHERE filter (10K)")]
    public async Task SelectFilter10K()
    {
        TableCatalog catalog = BuildCatalog();
        FunctionRegistry functions = BuildFunctions();
        QueryExpression query = SqlParser.Parse("SELECT id, name, value FROM data WHERE value > 500");
        QueryPlanner planner = new(catalog, functions);
        QueryOperator root = planner.Plan(query);
        ExecutionContext context = catalog.CreateExecutionContext();

        await foreach (RowBatch batch in root.ExecuteAsync(context))
        {
            context.ReturnRowBatch(batch);
        }
    }

    [Benchmark(Description = "SELECT with projection (10K)")]
    public async Task SelectProjection10K()
    {
        TableCatalog catalog = BuildCatalog();
        FunctionRegistry functions = BuildFunctions();
        QueryExpression query = SqlParser.Parse("SELECT id, name FROM data");
        QueryPlanner planner = new(catalog, functions);
        QueryOperator root = planner.Plan(query);
        ExecutionContext context = catalog.CreateExecutionContext();

        await foreach (RowBatch batch in root.ExecuteAsync(context))
        {
            context.ReturnRowBatch(batch);
        }
    }

    [Benchmark(Description = "INNER JOIN (10K x 1K)")]
    public async Task InnerJoin10Kx1K()
    {
        TableCatalog catalog = BuildCatalog(withLookup: true);
        FunctionRegistry functions = BuildFunctions();
        QueryExpression query = SqlParser.Parse(
            "SELECT a.id, a.name, b.description FROM data AS a INNER JOIN lookup AS b ON a.id = b.lookup_id");
        QueryPlanner planner = new(catalog, functions);
        QueryOperator root = planner.Plan(query);
        ExecutionContext context = catalog.CreateExecutionContext();

        await foreach (RowBatch batch in root.ExecuteAsync(context))
        {
            context.ReturnRowBatch(batch);
        }
    }

    [Benchmark(Description = "ORDER BY + LIMIT (10K)")]
    public async Task OrderByLimit10K()
    {
        TableCatalog catalog = BuildCatalog();
        FunctionRegistry functions = BuildFunctions();
        QueryExpression query = SqlParser.Parse("SELECT id, name, value FROM data ORDER BY value DESC LIMIT 100");
        QueryPlanner planner = new(catalog, functions);
        QueryOperator root = planner.Plan(query);
        ExecutionContext context = catalog.CreateExecutionContext();

        await foreach (RowBatch batch in root.ExecuteAsync(context))
        {
            context.ReturnRowBatch(batch);
        }
    }

    [Benchmark(Description = "Uncorrelated IN subquery (10K x 1K)")]
    public async Task UncorrelatedInSubquery()
    {
        TableCatalog catalog = BuildCatalog(withLookup: true);
        FunctionRegistry functions = BuildFunctions();
        QueryExpression query = SqlParser.Parse(
            "SELECT id, name FROM data WHERE id IN (SELECT lookup_id FROM lookup WHERE weight > 25)");
        QueryPlanner planner = new(catalog, functions);
        ExecutionContext context = catalog.CreateExecutionContext();
        QueryOperator root = await planner.PlanWithSubqueriesAsync(query, context, CancellationToken.None);

        await foreach (RowBatch batch in root.ExecuteAsync(context))
        {
            context.ReturnRowBatch(batch);
        }
    }

    [Benchmark(Description = "Correlated EXISTS semi-join (10K x 1K)")]
    public async Task CorrelatedExistsSemiJoin()
    {
        TableCatalog catalog = BuildCatalog(withLookup: true);
        FunctionRegistry functions = BuildFunctions();
        QueryExpression query = SqlParser.Parse(
            "SELECT data.id, data.name FROM data " +
            "WHERE EXISTS (SELECT 1 FROM lookup WHERE lookup.lookup_id = data.id AND weight > 25)");
        QueryPlanner planner = new(catalog, functions);
        ExecutionContext context = catalog.CreateExecutionContext();
        QueryOperator root = await planner.PlanWithSubqueriesAsync(query, context, CancellationToken.None);

        await foreach (RowBatch batch in root.ExecuteAsync(context))
        {
            context.ReturnRowBatch(batch);
        }
    }

    [Benchmark(Description = "Correlated NOT EXISTS anti-semi-join (10K x 1K)")]
    public async Task CorrelatedNotExistsAntiSemiJoin()
    {
        TableCatalog catalog = BuildCatalog(withLookup: true);
        FunctionRegistry functions = BuildFunctions();
        QueryExpression query = SqlParser.Parse(
            "SELECT data.id, data.name FROM data " +
            "WHERE NOT EXISTS (SELECT 1 FROM lookup WHERE lookup.lookup_id = data.id)");
        QueryPlanner planner = new(catalog, functions);
        ExecutionContext context = catalog.CreateExecutionContext();
        QueryOperator root = await planner.PlanWithSubqueriesAsync(query, context, CancellationToken.None);

        await foreach (RowBatch batch in root.ExecuteAsync(context))
        {
            context.ReturnRowBatch(batch);
        }
    }

    [Benchmark(Description = "Correlated scalar subquery (10K x 1K)")]
    public async Task CorrelatedScalarSubquery()
    {
        TableCatalog catalog = BuildCatalog(withLookup: true);
        FunctionRegistry functions = BuildFunctions();
        QueryExpression query = SqlParser.Parse(
            "SELECT data.id, data.name, (SELECT MAX(weight) FROM lookup WHERE lookup.lookup_id = data.id) AS max_weight FROM data");
        QueryPlanner planner = new(catalog, functions);
        ExecutionContext context = catalog.CreateExecutionContext();
        QueryOperator root = await planner.PlanWithSubqueriesAsync(query, context, CancellationToken.None);

        await foreach (RowBatch batch in root.ExecuteAsync(context))
        {
            context.ReturnRowBatch(batch);
        }
    }

    [Benchmark(Description = "SELECT DISTINCT low cardinality (10K)")]
    public async Task SelectDistinctLowCardinality()
    {
        TableCatalog catalog = BuildCatalog();
        FunctionRegistry functions = BuildFunctions();
        QueryExpression query = SqlParser.Parse("SELECT DISTINCT category FROM data");
        QueryPlanner planner = new(catalog, functions);
        QueryOperator root = planner.Plan(query);
        ExecutionContext context = catalog.CreateExecutionContext();

        await foreach (RowBatch batch in root.ExecuteAsync(context))
        {
            context.ReturnRowBatch(batch);
        }
    }

    [Benchmark(Description = "SELECT DISTINCT high cardinality (10K)")]
    public async Task SelectDistinctHighCardinality()
    {
        TableCatalog catalog = BuildCatalog();
        FunctionRegistry functions = BuildFunctions();
        QueryExpression query = SqlParser.Parse("SELECT DISTINCT id, category FROM data");
        QueryPlanner planner = new(catalog, functions);
        QueryOperator root = planner.Plan(query);
        ExecutionContext context = catalog.CreateExecutionContext();

        await foreach (RowBatch batch in root.ExecuteAsync(context))
        {
            context.ReturnRowBatch(batch);
        }
    }

    [Benchmark(Description = "COUNT(DISTINCT) per group (10K)")]
    public async Task CountDistinctPerGroup()
    {
        TableCatalog catalog = BuildCatalog();
        FunctionRegistry functions = BuildFunctions();
        QueryExpression query = SqlParser.Parse(
            "SELECT category, COUNT(DISTINCT name) AS unique_names FROM data GROUP BY category");
        QueryPlanner planner = new(catalog, functions);
        QueryOperator root = planner.Plan(query);
        ExecutionContext context = catalog.CreateExecutionContext();

        await foreach (RowBatch batch in root.ExecuteAsync(context))
        {
            context.ReturnRowBatch(batch);
        }
    }

    [Benchmark(Description = "CTE inlined single ref (10K)")]
    public async Task CommonTableExpressionInlinedSingleReference()
    {
        TableCatalog catalog = BuildCatalog();
        FunctionRegistry functions = BuildFunctions();
        QueryExpression query = SqlParser.Parse(
            "WITH filtered AS (SELECT id, name, value FROM data WHERE value > 500) SELECT id, name FROM filtered");
        QueryPlanner planner = new(catalog, functions);
        QueryOperator root = planner.Plan(query);
        ExecutionContext context = catalog.CreateExecutionContext();

        await foreach (RowBatch batch in root.ExecuteAsync(context))
        {
            context.ReturnRowBatch(batch);
        }
    }

    [Benchmark(Description = "CTE materialized multi-ref (10K)")]
    public async Task CommonTableExpressionMaterializedMultiReference()
    {
        TableCatalog catalog = BuildCatalog();
        FunctionRegistry functions = BuildFunctions();
        QueryExpression query = SqlParser.Parse(
            "WITH stats AS (SELECT category, AVG(value) AS avg_val, COUNT(*) AS cnt FROM data GROUP BY category) " +
            "SELECT a.category, a.avg_val, b.cnt FROM stats AS a INNER JOIN stats AS b ON a.category = b.category");
        QueryPlanner planner = new(catalog, functions);
        QueryOperator root = planner.Plan(query);
        ExecutionContext context = catalog.CreateExecutionContext();

        await foreach (RowBatch batch in root.ExecuteAsync(context))
        {
            context.ReturnRowBatch(batch);
        }
    }

    [Benchmark(Description = "Multi-CTE chained (10K)")]
    public async Task MultipleCommonTableExpressionsChained()
    {
        TableCatalog catalog = BuildCatalog();
        FunctionRegistry functions = BuildFunctions();
        QueryExpression query = SqlParser.Parse(
            "WITH high AS (SELECT id, name, value FROM data WHERE value > 500), " +
            "top_high AS (SELECT id, name, value FROM high ORDER BY value DESC LIMIT 100) " +
            "SELECT id, name FROM top_high");
        QueryPlanner planner = new(catalog, functions);
        QueryOperator root = planner.Plan(query);
        ExecutionContext context = catalog.CreateExecutionContext();

        await foreach (RowBatch batch in root.ExecuteAsync(context))
        {
            context.ReturnRowBatch(batch);
        }
    }

    [Benchmark(Description = "Recursive CTE 100 iterations")]
    public async Task RecursiveCommonTableExpression100()
    {
        TableCatalog catalog = BuildCatalog();
        FunctionRegistry functions = BuildFunctions();
        QueryExpression query = SqlParser.Parse(
            "WITH RECURSIVE seq AS (SELECT 1 AS n UNION ALL SELECT n + 1 AS n FROM seq WHERE n < 100) SELECT n FROM seq");
        QueryPlanner planner = new(catalog, functions);
        QueryOperator root = planner.Plan(query);
        ExecutionContext context = catalog.CreateExecutionContext();

        await foreach (RowBatch batch in root.ExecuteAsync(context))
        {
            context.ReturnRowBatch(batch);
        }
    }

    [Benchmark(Description = "Recursive CTE 1000 iterations")]
    public async Task RecursiveCommonTableExpression1000()
    {
        TableCatalog catalog = BuildCatalog();
        FunctionRegistry functions = BuildFunctions();
        QueryExpression query = SqlParser.Parse(
            "WITH RECURSIVE seq AS (SELECT 1 AS n UNION ALL SELECT n + 1 AS n FROM seq WHERE n < 1000) SELECT n FROM seq");
        QueryPlanner planner = new(catalog, functions);
        QueryOperator root = planner.Plan(query);
        ExecutionContext context = catalog.CreateExecutionContext();

        await foreach (RowBatch batch in root.ExecuteAsync(context))
        {
            context.ReturnRowBatch(batch);
        }
    }

    [Benchmark(Description = "UNION ALL two tables (10K + 10K)")]
    public async Task UnionAllTwoTables()
    {
        TableCatalog catalog = BuildCatalog(withDataSecond: true);
        FunctionRegistry functions = BuildFunctions();
        QueryExpression query = SqlParser.Parse(
            "SELECT id, name, value FROM data UNION ALL SELECT id, name, value FROM data_b");
        QueryPlanner planner = new(catalog, functions);
        QueryOperator root = planner.Plan(query);
        ExecutionContext context = catalog.CreateExecutionContext();

        await foreach (RowBatch batch in root.ExecuteAsync(context))
        {
            context.ReturnRowBatch(batch);
        }
    }

    [Benchmark(Description = "UNION DISTINCT two tables (10K + 10K)")]
    public async Task UnionDistinctTwoTables()
    {
        TableCatalog catalog = BuildCatalog(withDataSecond: true);
        FunctionRegistry functions = BuildFunctions();
        QueryExpression query = SqlParser.Parse(
            "SELECT id, name, value FROM data UNION SELECT id, name, value FROM data_b");
        QueryPlanner planner = new(catalog, functions);
        QueryOperator root = planner.Plan(query);
        ExecutionContext context = catalog.CreateExecutionContext();

        await foreach (RowBatch batch in root.ExecuteAsync(context))
        {
            context.ReturnRowBatch(batch);
        }
    }

    [Benchmark(Description = "INTERSECT DISTINCT two tables (10K x 10K)")]
    public async Task IntersectDistinctTwoTables()
    {
        TableCatalog catalog = BuildCatalog(withDataSecond: true);
        FunctionRegistry functions = BuildFunctions();
        QueryExpression query = SqlParser.Parse(
            "SELECT category FROM data INTERSECT SELECT category FROM data_b");
        QueryPlanner planner = new(catalog, functions);
        QueryOperator root = planner.Plan(query);
        ExecutionContext context = catalog.CreateExecutionContext();

        await foreach (RowBatch batch in root.ExecuteAsync(context))
        {
            context.ReturnRowBatch(batch);
        }
    }

    [Benchmark(Description = "EXCEPT DISTINCT two tables (10K \\ 10K)")]
    public async Task ExceptDistinctTwoTables()
    {
        TableCatalog catalog = BuildCatalog(withDataSecond: true);
        FunctionRegistry functions = BuildFunctions();
        QueryExpression query = SqlParser.Parse(
            "SELECT id, name FROM data EXCEPT SELECT id, name FROM data_b");
        QueryPlanner planner = new(catalog, functions);
        QueryOperator root = planner.Plan(query);
        ExecutionContext context = catalog.CreateExecutionContext();

        await foreach (RowBatch batch in root.ExecuteAsync(context))
        {
            context.ReturnRowBatch(batch);
        }
    }

    [Benchmark(Description = "UNION ALL same table filtered (10K)")]
    public async Task UnionAllSameTableFiltered()
    {
        TableCatalog catalog = BuildCatalog();
        FunctionRegistry functions = BuildFunctions();
        QueryExpression query = SqlParser.Parse(
            "SELECT id, name, value FROM data WHERE value > 800 " +
            "UNION ALL " +
            "SELECT id, name, value FROM data WHERE value < 200");
        QueryPlanner planner = new(catalog, functions);
        QueryOperator root = planner.Plan(query);
        ExecutionContext context = catalog.CreateExecutionContext();

        await foreach (RowBatch batch in root.ExecuteAsync(context))
        {
            context.ReturnRowBatch(batch);
        }
    }

    [Benchmark(Description = "Chained UNION ALL three-way (10K + 10K + 10K)")]
    public async Task ChainedUnionAllThreeWay()
    {
        TableCatalog catalog = BuildCatalog(withDataSecond: true);
        FunctionRegistry functions = BuildFunctions();
        QueryExpression query = SqlParser.Parse(
            "SELECT id, name FROM data " +
            "UNION ALL SELECT id, name FROM data_b " +
            "UNION ALL SELECT id, name FROM data");
        QueryPlanner planner = new(catalog, functions);
        QueryOperator root = planner.Plan(query);
        ExecutionContext context = catalog.CreateExecutionContext();

        await foreach (RowBatch batch in root.ExecuteAsync(context))
        {
            context.ReturnRowBatch(batch);
        }
    }
}