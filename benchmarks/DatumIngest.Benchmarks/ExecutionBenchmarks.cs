using BenchmarkDotNet.Attributes;
using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar;
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
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.Register(new TableDescriptor("csv", "data", dataPath, new Dictionary<string, string>()));
        if (lookupPath is not null)
        {
            catalog.Register(new TableDescriptor("csv", "lookup", lookupPath, new Dictionary<string, string>()));
        }
        return catalog;
    }

    private static FunctionRegistry BuildFunctions()
    {
        FunctionRegistry registry = new();
        registry.RegisterScalar(new LenFunction());
        registry.RegisterScalar(new CastFunction());
        registry.RegisterScalar(new NormalizeFunction());
        registry.RegisterScalar(new ClampFunction());
        return registry;
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
}
