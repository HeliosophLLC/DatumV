using BenchmarkDotNet.Attributes;
using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Benchmarks;

/// <summary>
/// Benchmarks for full query execution including scan, filter, project, and join.
/// </summary>
[MemoryDiagnoser]
public class ExecutionBenchmarks
{
    private string _tempDirectory = null!;
    private string _csvPath = null!;
    private string _lookupCsvPath = null!;

    [GlobalSetup]
    public void Setup()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "datum_bench_execution");
        Directory.CreateDirectory(_tempDirectory);

        _csvPath = SyntheticDataGenerator.GenerateCsv(_tempDirectory, 10_000);

        // Generate a lookup CSV for join benchmarks
        string lookupPath = Path.Combine(_tempDirectory, "lookup.csv");
        using StreamWriter writer = new(lookupPath);
        writer.WriteLine("lookup_id,description,weight");
        Random random = new(42);
        for (int i = 0; i < 1_000; i++)
        {
            writer.WriteLine($"{i},desc_{i:D6},{random.NextDouble() * 50.0:F4}");
        }
        _lookupCsvPath = lookupPath;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private static TableCatalog BuildCatalog(string dataPath, string? lookupPath = null)
    {
        TableCatalog catalog = new();
        catalog.Register(new TableDescriptor("csv", "data", dataPath, new Dictionary<string, string>()));
        if (lookupPath is not null)
        {
            catalog.Register(new TableDescriptor("csv", "lookup", lookupPath, new Dictionary<string, string>()));
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
        TableCatalog catalog = BuildCatalog(_csvPath);
        FunctionRegistry functions = BuildFunctions();
        SelectStatement statement = SqlParser.Parse("SELECT * FROM data");
        QueryPlanner planner = new(catalog, functions);
        IQueryOperator root = planner.Plan(statement);
        ExecutionContext context = new(CancellationToken.None, functions, catalog);

        await foreach (Row _ in root.ExecuteAsync(context))
        {
        }
    }

    [Benchmark(Description = "SELECT with WHERE filter (10K)")]
    public async Task SelectFilter10K()
    {
        TableCatalog catalog = BuildCatalog(_csvPath);
        FunctionRegistry functions = BuildFunctions();
        SelectStatement statement = SqlParser.Parse("SELECT id, name, value FROM data WHERE value > 500");
        QueryPlanner planner = new(catalog, functions);
        IQueryOperator root = planner.Plan(statement);
        ExecutionContext context = new(CancellationToken.None, functions, catalog);

        await foreach (Row _ in root.ExecuteAsync(context))
        {
        }
    }

    [Benchmark(Description = "SELECT with projection (10K)")]
    public async Task SelectProjection10K()
    {
        TableCatalog catalog = BuildCatalog(_csvPath);
        FunctionRegistry functions = BuildFunctions();
        SelectStatement statement = SqlParser.Parse("SELECT id, name FROM data");
        QueryPlanner planner = new(catalog, functions);
        IQueryOperator root = planner.Plan(statement);
        ExecutionContext context = new(CancellationToken.None, functions, catalog);

        await foreach (Row _ in root.ExecuteAsync(context))
        {
        }
    }

    [Benchmark(Description = "INNER JOIN (10K x 1K)")]
    public async Task InnerJoin10Kx1K()
    {
        TableCatalog catalog = BuildCatalog(_csvPath, _lookupCsvPath);
        FunctionRegistry functions = BuildFunctions();
        SelectStatement statement = SqlParser.Parse(
            "SELECT a.id, a.name, b.description FROM data AS a INNER JOIN lookup AS b ON a.id = b.lookup_id");
        QueryPlanner planner = new(catalog, functions);
        IQueryOperator root = planner.Plan(statement);
        ExecutionContext context = new(CancellationToken.None, functions, catalog);

        await foreach (Row _ in root.ExecuteAsync(context))
        {
        }
    }

    [Benchmark(Description = "ORDER BY + LIMIT (10K)")]
    public async Task OrderByLimit10K()
    {
        TableCatalog catalog = BuildCatalog(_csvPath);
        FunctionRegistry functions = BuildFunctions();
        SelectStatement statement = SqlParser.Parse("SELECT id, name, value FROM data ORDER BY value DESC LIMIT 100");
        QueryPlanner planner = new(catalog, functions);
        IQueryOperator root = planner.Plan(statement);
        ExecutionContext context = new(CancellationToken.None, functions, catalog);

        await foreach (Row _ in root.ExecuteAsync(context))
        {
        }
    }

    [Benchmark(Description = "Uncorrelated IN subquery (10K x 1K)")]
    public async Task UncorrelatedInSubquery()
    {
        TableCatalog catalog = BuildCatalog(_csvPath, _lookupCsvPath);
        FunctionRegistry functions = BuildFunctions();
        SelectStatement statement = SqlParser.Parse(
            "SELECT id, name FROM data WHERE id IN (SELECT lookup_id FROM lookup WHERE weight > 25)");
        QueryPlanner planner = new(catalog, functions);
        ExecutionContext context = new(CancellationToken.None, functions, catalog);
        IQueryOperator root = await planner.PlanWithSubqueriesAsync(statement, context, CancellationToken.None);

        await foreach (Row _ in root.ExecuteAsync(context))
        {
        }
    }

    [Benchmark(Description = "Correlated EXISTS semi-join (10K x 1K)")]
    public async Task CorrelatedExistsSemiJoin()
    {
        TableCatalog catalog = BuildCatalog(_csvPath, _lookupCsvPath);
        FunctionRegistry functions = BuildFunctions();
        SelectStatement statement = SqlParser.Parse(
            "SELECT data.id, data.name FROM data " +
            "WHERE EXISTS (SELECT 1 FROM lookup WHERE lookup.lookup_id = data.id AND weight > 25)");
        QueryPlanner planner = new(catalog, functions);
        ExecutionContext context = new(CancellationToken.None, functions, catalog);
        IQueryOperator root = await planner.PlanWithSubqueriesAsync(statement, context, CancellationToken.None);

        await foreach (Row _ in root.ExecuteAsync(context))
        {
        }
    }

    [Benchmark(Description = "Correlated NOT EXISTS anti-semi-join (10K x 1K)")]
    public async Task CorrelatedNotExistsAntiSemiJoin()
    {
        TableCatalog catalog = BuildCatalog(_csvPath, _lookupCsvPath);
        FunctionRegistry functions = BuildFunctions();
        SelectStatement statement = SqlParser.Parse(
            "SELECT data.id, data.name FROM data " +
            "WHERE NOT EXISTS (SELECT 1 FROM lookup WHERE lookup.lookup_id = data.id)");
        QueryPlanner planner = new(catalog, functions);
        ExecutionContext context = new(CancellationToken.None, functions, catalog);
        IQueryOperator root = await planner.PlanWithSubqueriesAsync(statement, context, CancellationToken.None);

        await foreach (Row _ in root.ExecuteAsync(context))
        {
        }
    }

    [Benchmark(Description = "Correlated scalar subquery (10K x 1K)")]
    public async Task CorrelatedScalarSubquery()
    {
        TableCatalog catalog = BuildCatalog(_csvPath, _lookupCsvPath);
        FunctionRegistry functions = BuildFunctions();
        SelectStatement statement = SqlParser.Parse(
            "SELECT data.id, data.name, (SELECT MAX(weight) FROM lookup WHERE lookup.lookup_id = data.id) AS max_weight FROM data");
        QueryPlanner planner = new(catalog, functions);
        ExecutionContext context = new(CancellationToken.None, functions, catalog);
        IQueryOperator root = await planner.PlanWithSubqueriesAsync(statement, context, CancellationToken.None);

        await foreach (Row _ in root.ExecuteAsync(context))
        {
        }
    }

    [Benchmark(Description = "SELECT DISTINCT low cardinality (10K)")]
    public async Task SelectDistinctLowCardinality()
    {
        TableCatalog catalog = BuildCatalog(_csvPath);
        FunctionRegistry functions = BuildFunctions();
        SelectStatement statement = SqlParser.Parse("SELECT DISTINCT category FROM data");
        QueryPlanner planner = new(catalog, functions);
        IQueryOperator root = planner.Plan(statement);
        ExecutionContext context = new(CancellationToken.None, functions, catalog);

        await foreach (Row _ in root.ExecuteAsync(context))
        {
        }
    }

    [Benchmark(Description = "SELECT DISTINCT high cardinality (10K)")]
    public async Task SelectDistinctHighCardinality()
    {
        TableCatalog catalog = BuildCatalog(_csvPath);
        FunctionRegistry functions = BuildFunctions();
        SelectStatement statement = SqlParser.Parse("SELECT DISTINCT id, category FROM data");
        QueryPlanner planner = new(catalog, functions);
        IQueryOperator root = planner.Plan(statement);
        ExecutionContext context = new(CancellationToken.None, functions, catalog);

        await foreach (Row _ in root.ExecuteAsync(context))
        {
        }
    }

    [Benchmark(Description = "COUNT(DISTINCT) per group (10K)")]
    public async Task CountDistinctPerGroup()
    {
        TableCatalog catalog = BuildCatalog(_csvPath);
        FunctionRegistry functions = BuildFunctions();
        SelectStatement statement = SqlParser.Parse(
            "SELECT category, COUNT(DISTINCT name) AS unique_names FROM data GROUP BY category");
        QueryPlanner planner = new(catalog, functions);
        IQueryOperator root = planner.Plan(statement);
        ExecutionContext context = new(CancellationToken.None, functions, catalog);

        await foreach (Row _ in root.ExecuteAsync(context))
        {
        }
    }
}
