#if false
using System.Globalization;

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
/// Benchmarks for PIVOT and UNPIVOT operators across realistic dataset shapes and sizes.
/// </summary>
/// <remarks>
/// The data model used throughout:
/// <list type="bullet">
///   <item><term>Long format</term><description>
///     Columns: <c>group_id</c> (int), <c>region</c> (string, 4 values), <c>revenue</c> (float).
///     Used as the source for PIVOT benchmarks.
///   </description></item>
///   <item><term>Wide format</term><description>
///     Columns: <c>group_id</c> (int), <c>north</c>, <c>south</c>, <c>east</c>, <c>west</c> (float).
///     Used as the source for UNPIVOT benchmarks.
///   </description></item>
/// </list>
/// </remarks>
[MemoryDiagnoser]
public class PivotBenchmarks
{
    private string _tempDirectory = null!;
    private string _longFormat5KPath = null!;
    private string _longFormat20KPath = null!;
    private string _wideFormat1250Path = null!;
    private string _wideFormat5KPath = null!;

    [GlobalSetup]
    public void Setup()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "datum_bench_pivot");
        Directory.CreateDirectory(_tempDirectory);

        // Long-format data for PIVOT: group_id, region, revenue
        // 5K rows = 1250 groups × 4 regions
        _longFormat5KPath = GenerateLongFormatCsv(_tempDirectory, groupCount: 1_250);
        // 20K rows = 5000 groups × 4 regions
        _longFormat20KPath = GenerateLongFormatCsv(_tempDirectory, groupCount: 5_000);

        // Wide-format data for UNPIVOT: group_id, north, south, east, west
        _wideFormat1250Path = GenerateWideFormatCsv(_tempDirectory, rowCount: 1_250);
        _wideFormat5KPath = GenerateWideFormatCsv(_tempDirectory, rowCount: 5_000);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // PIVOT — explicit IN list (schema known at parse time)
    // -------------------------------------------------------------------------

    [Benchmark(Description = "PIVOT explicit IN list, AVG(revenue) (5K rows → 1250 output)")]
    public async Task PivotExplicitValues5K()
    {
        await ExecuteQuery(
            _longFormat5KPath,
            "SELECT * FROM data PIVOT (AVG(revenue) FOR region IN ('north', 'south', 'east', 'west'))");
    }

    [Benchmark(Description = "PIVOT explicit IN list, AVG(revenue) (20K rows → 5000 output)")]
    public async Task PivotExplicitValues20K()
    {
        await ExecuteQuery(
            _longFormat20KPath,
            "SELECT * FROM data PIVOT (AVG(revenue) FOR region IN ('north', 'south', 'east', 'west'))");
    }

    // -------------------------------------------------------------------------
    // PIVOT — auto-discover (must buffer all rows before schema is known)
    // -------------------------------------------------------------------------

    [Benchmark(Description = "PIVOT auto-discover, AVG(revenue) (5K rows → 1250 output)")]
    public async Task PivotAutoDiscover5K()
    {
        await ExecuteQuery(
            _longFormat5KPath,
            "SELECT * FROM data PIVOT (AVG(revenue) FOR region)");
    }

    [Benchmark(Description = "PIVOT auto-discover, AVG(revenue) (20K rows → 5000 output)")]
    public async Task PivotAutoDiscover20K()
    {
        await ExecuteQuery(
            _longFormat20KPath,
            "SELECT * FROM data PIVOT (AVG(revenue) FOR region)");
    }

    // -------------------------------------------------------------------------
    // PIVOT — multiple aggregates in a single PIVOT clause
    // -------------------------------------------------------------------------

    [Benchmark(Description = "PIVOT two aggregates SUM+COUNT (5K rows → 1250 output, 8 value cols)")]
    public async Task PivotMultipleAggregates5K()
    {
        await ExecuteQuery(
            _longFormat5KPath,
            "SELECT * FROM data PIVOT (SUM(revenue), COUNT(revenue) FOR region IN ('north', 'south', 'east', 'west'))");
    }

    // -------------------------------------------------------------------------
    // UNPIVOT — streaming wide-to-long rotation
    // -------------------------------------------------------------------------

    [Benchmark(Description = "UNPIVOT 4 columns (1250 wide rows → 5000 output)")]
    public async Task UnpivotWide1250()
    {
        await ExecuteQuery(
            _wideFormat1250Path,
            "SELECT * FROM data UNPIVOT (revenue FOR region IN (north, south, east, west))");
    }

    [Benchmark(Description = "UNPIVOT 4 columns (5000 wide rows → 20K output)")]
    public async Task UnpivotWide5K()
    {
        await ExecuteQuery(
            _wideFormat5KPath,
            "SELECT * FROM data UNPIVOT (revenue FOR region IN (north, south, east, west))");
    }

    [Benchmark(Description = "UNPIVOT INCLUDE NULLS 4 columns (1250 wide rows)")]
    public async Task UnpivotIncludeNulls1250()
    {
        await ExecuteQuery(
            _wideFormat1250Path,
            "SELECT * FROM data UNPIVOT INCLUDE NULLS (revenue FOR region IN (north, south, east, west))");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static async Task ExecuteQuery(string csvPath, string sql)
    {
        TableCatalog catalog = new();
        catalog.Register(new TableDescriptor("csv", "data", csvPath, new Dictionary<string, string>()));
        FunctionRegistry functions = FunctionRegistry.CreateDefault();
        QueryExpression query = SqlParser.Parse(sql);
        QueryPlanner planner = new(catalog, functions);
        IQueryOperator root = planner.Plan(query);
        ExecutionContext context = catalog.CreateExecutionContext();

        await foreach (RowBatch batch in root.ExecuteAsync(context))
        {
            batch.Return();
        }
    }

    /// <summary>
    /// Generates a long-format CSV with columns: <c>group_id</c>, <c>region</c>, <c>revenue</c>.
    /// Emits <paramref name="groupCount"/> × 4 rows (one row per group per region).
    /// </summary>
    private static string GenerateLongFormatCsv(string directory, int groupCount)
    {
        int totalRows = groupCount * 4;
        string filePath = Path.Combine(directory, $"long_{totalRows}.csv");
        using StreamWriter writer = new(filePath);
        writer.WriteLine("group_id,region,revenue");

        string[] regions = ["north", "south", "east", "west"];
        Random random = new(42);

        for (int groupId = 0; groupId < groupCount; groupId++)
        {
            foreach (string region in regions)
            {
                float revenue = (float)(random.NextDouble() * 10_000.0);
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{groupId},{region},{revenue:F2}"));
            }
        }

        return filePath;
    }

    /// <summary>
    /// Generates a wide-format CSV with columns: <c>group_id</c>, <c>north</c>, <c>south</c>,
    /// <c>east</c>, <c>west</c>. Emits <paramref name="rowCount"/> rows.
    /// </summary>
    private static string GenerateWideFormatCsv(string directory, int rowCount)
    {
        string filePath = Path.Combine(directory, $"wide_{rowCount}.csv");
        using StreamWriter writer = new(filePath);
        writer.WriteLine("group_id,north,south,east,west");

        Random random = new(42);

        for (int groupId = 0; groupId < rowCount; groupId++)
        {
            float north = (float)(random.NextDouble() * 10_000.0);
            float south = (float)(random.NextDouble() * 10_000.0);
            float east = (float)(random.NextDouble() * 10_000.0);
            float west = (float)(random.NextDouble() * 10_000.0);
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{groupId},{north:F2},{south:F2},{east:F2},{west:F2}"));
        }

        return filePath;
    }
}
#endif