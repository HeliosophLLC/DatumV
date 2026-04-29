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
/// Benchmarks for PIVOT and UNPIVOT operators across realistic dataset shapes and sizes.
/// </summary>
/// <remarks>
/// Two data shapes:
/// <list type="bullet">
///   <item><term>Long format</term><description>
///     Columns: <c>group_id</c> (Float32), <c>region</c> (String, 4 values), <c>revenue</c> (Float32).
///     Source for PIVOT benchmarks.
///   </description></item>
///   <item><term>Wide format</term><description>
///     Columns: <c>group_id</c> (Float32), <c>north</c>, <c>south</c>, <c>east</c>, <c>west</c> (Float32).
///     Source for UNPIVOT benchmarks.
///   </description></item>
/// </list>
/// Each benchmark runs against both <see cref="ProviderKind.InMemory"/> and
/// <see cref="ProviderKind.DatumFile"/>; the report shows the cost split between
/// pure operator work and end-to-end ingestion + execute.
/// </remarks>
[MemoryDiagnoser]
public class PivotBenchmarks
{
    private static readonly string[] LongColumns = ["group_id", "region", "revenue"];
    private static readonly string[] WideColumns = ["group_id", "north", "south", "east", "west"];
    private static readonly string[] Regions = ["north", "south", "east", "west"];

    private static readonly ColumnDescriptorV2[] LongDescriptors =
    [
        new("group_id", DataKind.Float32, EncoderKind.FixedWidth, IsNullable: false),
        new("region", DataKind.String, EncoderKind.VariableSlot, IsNullable: false),
        new("revenue", DataKind.Float32, EncoderKind.FixedWidth, IsNullable: false),
    ];

    private static readonly ColumnDescriptorV2[] WideDescriptors =
    [
        new("group_id", DataKind.Float32, EncoderKind.FixedWidth, IsNullable: false),
        new("north", DataKind.Float32, EncoderKind.FixedWidth, IsNullable: false),
        new("south", DataKind.Float32, EncoderKind.FixedWidth, IsNullable: false),
        new("east", DataKind.Float32, EncoderKind.FixedWidth, IsNullable: false),
        new("west", DataKind.Float32, EncoderKind.FixedWidth, IsNullable: false),
    ];

    [Params(ProviderKind.InMemory, ProviderKind.DatumFile)]
    public ProviderKind Provider;

    private Pool _pool = null!;
    private object?[][] _long5K = null!;
    private object?[][] _long20K = null!;
    private object?[][] _wide1250 = null!;
    private object?[][] _wide5K = null!;

    // Populated only when Provider == ProviderKind.DatumFile.
    private string? _tempDir;
    private string? _long5KFile;
    private string? _long20KFile;
    private string? _wide1250File;
    private string? _wide5KFile;

    [GlobalSetup]
    public void Setup()
    {
        _pool = new Pool(new PoolBacking());

        // Long-format: groupCount × 4 regions rows.
        _long5K = GenerateLong(groupCount: 1_250);   // 5K rows
        _long20K = GenerateLong(groupCount: 5_000);  // 20K rows

        // Wide-format: one row per group.
        _wide1250 = GenerateWide(rowCount: 1_250);
        _wide5K = GenerateWide(rowCount: 5_000);

        if (Provider == ProviderKind.DatumFile)
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"datum_bench_pivot_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
            _long5KFile = DatumFileBenchHelper.WriteRawRowsToDatumFile(
                _pool, Path.Combine(_tempDir, "long_5k.datum"), _long5K, LongDescriptors);
            _long20KFile = DatumFileBenchHelper.WriteRawRowsToDatumFile(
                _pool, Path.Combine(_tempDir, "long_20k.datum"), _long20K, LongDescriptors);
            _wide1250File = DatumFileBenchHelper.WriteRawRowsToDatumFile(
                _pool, Path.Combine(_tempDir, "wide_1250.datum"), _wide1250, WideDescriptors);
            _wide5KFile = DatumFileBenchHelper.WriteRawRowsToDatumFile(
                _pool, Path.Combine(_tempDir, "wide_5k.datum"), _wide5K, WideDescriptors);
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

    private TableCatalog BuildCatalog(object?[][] rows, string[] columns, string datumFilePath)
    {
        TableCatalog catalog = new(_pool);
        if (Provider == ProviderKind.InMemory)
        {
            catalog.Add(new InMemoryTableProvider(_pool, "data", columns, rows));
        }
        else
        {
            catalog.Add(new TableDescriptor("data", datumFilePath));
        }
        return catalog;
    }

    // -------------------------------------------------------------------------
    // PIVOT — explicit IN list (schema known at parse time)
    // -------------------------------------------------------------------------

    [Benchmark(Description = "PIVOT explicit IN list, AVG(revenue) (5K rows → 1250 output)")]
    public Task PivotExplicitValues5K() => ExecuteQuery(
        _long5K, LongColumns, _long5KFile!,
        "SELECT * FROM data PIVOT (AVG(revenue) FOR region IN ('north', 'south', 'east', 'west'))");

    [Benchmark(Description = "PIVOT explicit IN list, AVG(revenue) (20K rows → 5000 output)")]
    public Task PivotExplicitValues20K() => ExecuteQuery(
        _long20K, LongColumns, _long20KFile!,
        "SELECT * FROM data PIVOT (AVG(revenue) FOR region IN ('north', 'south', 'east', 'west'))");

    // -------------------------------------------------------------------------
    // PIVOT — auto-discover (must walk every row to discover distinct values)
    // -------------------------------------------------------------------------

    [Benchmark(Description = "PIVOT auto-discover, AVG(revenue) (5K rows → 1250 output)")]
    public Task PivotAutoDiscover5K() => ExecuteQuery(
        _long5K, LongColumns, _long5KFile!,
        "SELECT * FROM data PIVOT (AVG(revenue) FOR region)");

    [Benchmark(Description = "PIVOT auto-discover, AVG(revenue) (20K rows → 5000 output)")]
    public Task PivotAutoDiscover20K() => ExecuteQuery(
        _long20K, LongColumns, _long20KFile!,
        "SELECT * FROM data PIVOT (AVG(revenue) FOR region)");

    // -------------------------------------------------------------------------
    // PIVOT — multiple aggregates in a single PIVOT clause
    // -------------------------------------------------------------------------

    [Benchmark(Description = "PIVOT two aggregates SUM+COUNT (5K rows → 1250 output, 8 value cols)")]
    public Task PivotMultipleAggregates5K() => ExecuteQuery(
        _long5K, LongColumns, _long5KFile!,
        "SELECT * FROM data PIVOT (SUM(revenue), COUNT(revenue) FOR region IN ('north', 'south', 'east', 'west'))");

    // -------------------------------------------------------------------------
    // UNPIVOT — streaming wide-to-long rotation
    // -------------------------------------------------------------------------

    [Benchmark(Description = "UNPIVOT 4 columns (1250 wide rows → 5000 output)")]
    public Task UnpivotWide1250() => ExecuteQuery(
        _wide1250, WideColumns, _wide1250File!,
        "SELECT * FROM data UNPIVOT (revenue FOR region IN (north, south, east, west))");

    [Benchmark(Description = "UNPIVOT 4 columns (5000 wide rows → 20K output)")]
    public Task UnpivotWide5K() => ExecuteQuery(
        _wide5K, WideColumns, _wide5KFile!,
        "SELECT * FROM data UNPIVOT (revenue FOR region IN (north, south, east, west))");

    [Benchmark(Description = "UNPIVOT INCLUDE NULLS 4 columns (1250 wide rows)")]
    public Task UnpivotIncludeNulls1250() => ExecuteQuery(
        _wide1250, WideColumns, _wide1250File!,
        "SELECT * FROM data UNPIVOT INCLUDE NULLS (revenue FOR region IN (north, south, east, west))");

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task ExecuteQuery(object?[][] rows, string[] columns, string datumFilePath, string sql)
    {
        TableCatalog catalog = BuildCatalog(rows, columns, datumFilePath);
        FunctionRegistry functions = FunctionRegistry.CreateDefault();
        QueryExpression query = SqlParser.Parse(sql);
        QueryPlanner planner = new(catalog, functions);
        QueryOperator root = planner.Plan(query);
        ExecutionContext context = catalog.CreateExecutionContext();

        await foreach (RowBatch batch in root.ExecuteAsync(context))
        {
            context.ReturnRowBatch(batch);
        }
    }

    private static object?[][] GenerateLong(int groupCount)
    {
        Random random = new(42);
        object?[][] rows = new object?[groupCount * Regions.Length][];
        int index = 0;
        for (int g = 0; g < groupCount; g++)
        {
            foreach (string region in Regions)
            {
                rows[index++] = [(float)g, region, (float)(random.NextDouble() * 10_000.0)];
            }
        }
        return rows;
    }

    private static object?[][] GenerateWide(int rowCount)
    {
        Random random = new(42);
        object?[][] rows = new object?[rowCount][];
        for (int g = 0; g < rowCount; g++)
        {
            rows[g] = [
                (float)g,
                (float)(random.NextDouble() * 10_000.0),
                (float)(random.NextDouble() * 10_000.0),
                (float)(random.NextDouble() * 10_000.0),
                (float)(random.NextDouble() * 10_000.0),
            ];
        }
        return rows;
    }
}
