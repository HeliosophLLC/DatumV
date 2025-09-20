using System.Globalization;
using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Allocation regression tests that measure GC pressure during query execution
/// over synthetic CSV data. Each test runs a warmup pass (to JIT and prime pools),
/// then measures the second pass with <see cref="GC.GetAllocatedBytesForCurrentThread"/>.
///
/// Thresholds are set at roughly 2× the calibrated baseline (20K rows, .NET 10,
/// Release build, April 2026) to absorb normal variance while catching real
/// regressions — e.g., an accidental <c>.ToList()</c> in a streaming path or
/// boxing in a hot loop.
/// </summary>
[Trait("Category", "Allocation")]
public sealed class AllocationTests : IDisposable
{
    private const int RowCount = 20_000;
    private const int LookupRowCount = 2_000;

    private readonly string _tempDir;
    private readonly string _csvPath;
    private readonly string _lookupCsvPath;
    private readonly string _compositeKeyLookupCsvPath;
    private readonly string _secondCsvPath;

    public AllocationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"datum_alloc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _csvPath = GenerateCsv("data.csv", RowCount, seed: 42);
        _lookupCsvPath = GenerateLookupCsv("lookup.csv", LookupRowCount, seed: 42);
        _compositeKeyLookupCsvPath = GenerateCompositeKeyLookupCsv("composite_lookup.csv", LookupRowCount, seed: 42);
        _secondCsvPath = GenerateCsv("data_b.csv", RowCount, seed: 99);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    // ──────────────── Scan / Project / Filter ────────────────

    [Fact]
    public async Task SelectAll_AllocationsBounded()
    {
        // Baseline: ~5.2 MB
        (long bytes, int gen2) = await MeasureQueryAsync(
            "SELECT * FROM data",
            BuildCatalog());

        AssertAllocations("SELECT *", bytes, gen2, maxBytes: 12_000_000);
    }

    [Fact]
    public async Task SelectProjection_AllocationsBounded()
    {
        // Baseline: ~4.5 MB
        (long bytes, int gen2) = await MeasureQueryAsync(
            "SELECT id, name FROM data",
            BuildCatalog());

        AssertAllocations("Projection", bytes, gen2, maxBytes: 10_000_000);
    }

    [Fact]
    public async Task WhereFilter_AllocationsBounded()
    {
        // Baseline: ~4.1 MB
        (long bytes, int gen2) = await MeasureQueryAsync(
            "SELECT id, name, value FROM data WHERE value > 500",
            BuildCatalog());

        AssertAllocations("WHERE filter", bytes, gen2, maxBytes: 10_000_000);
    }

    // ──────────────── Joins ────────────────

    [Fact]
    public async Task InnerJoin_AllocationsBounded()
    {
        // Baseline: ~4.9 MB
        (long bytes, int gen2) = await MeasureQueryAsync(
            "SELECT a.id, a.name, b.description FROM data AS a INNER JOIN lookup AS b ON a.id = b.lookup_id",
            BuildCatalog(withLookup: true));

        AssertAllocations("INNER JOIN", bytes, gen2, maxBytes: 12_000_000);
    }

    [Fact]
    public async Task LeftJoin_AllocationsBounded()
    {
        // Baseline: ~6.4 MB
        (long bytes, int gen2) = await MeasureQueryAsync(
            "SELECT a.id, b.description FROM data AS a LEFT JOIN lookup AS b ON a.id = b.lookup_id",
            BuildCatalog(withLookup: true));

        AssertAllocations("LEFT JOIN", bytes, gen2, maxBytes: 14_000_000);
    }

    [Fact]
    public async Task CompositeKeyJoin_AllocationsBounded()
    {
        // Exercises GraceHashJoinExecutor.EvaluateKeyParts() which allocates
        // a new DataValue[] per row for composite (multi-column) join keys.
        // A memory budget is set to force the GraceHashJoinExecutor path.
        // Before fix (per-row alloc): ~16.7 MB.
        // After fix (scratch buffer reuse): ~13.8 MB (~17% reduction).
        (long bytes, int gen2) = await MeasureQueryAsync(
            "SELECT a.id, a.name, b.description FROM data AS a INNER JOIN composite_lookup AS b ON a.id = b.lookup_id AND a.category = b.category",
            BuildCatalog(withCompositeKeyLookup: true),
            memoryBudgetBytes: 128 * 1024 * 1024);

        AssertAllocations("Composite-key JOIN", bytes, gen2, maxBytes: 15_000_000);
    }

    // ──────────────── Aggregation ────────────────

    [Fact]
    public async Task GroupByAggregate_AllocationsBounded()
    {
        // Baseline: ~2.8 MB
        (long bytes, int gen2) = await MeasureQueryAsync(
            "SELECT category, COUNT(*) AS cnt, AVG(value) AS avg_val FROM data GROUP BY category",
            BuildCatalog());

        AssertAllocations("GROUP BY", bytes, gen2, maxBytes: 7_000_000);
    }

    [Fact]
    public async Task CountDistinctPerGroup_AllocationsBounded()
    {
        // Baseline: ~4.3 MB
        (long bytes, int gen2) = await MeasureQueryAsync(
            "SELECT category, COUNT(DISTINCT name) AS unique_names FROM data GROUP BY category",
            BuildCatalog());

        AssertAllocations("COUNT(DISTINCT)", bytes, gen2, maxBytes: 10_000_000);
    }

    // ──────────────── Sorting ────────────────

    [Fact]
    public async Task OrderByLimit_AllocationsBounded()
    {
        // Baseline: ~6.2 MB
        (long bytes, int gen2) = await MeasureQueryAsync(
            "SELECT id, name, value FROM data ORDER BY value DESC LIMIT 100",
            BuildCatalog());

        AssertAllocations("ORDER BY LIMIT", bytes, gen2, maxBytes: 14_000_000);
    }

    [Fact]
    public async Task OrderByFull_AllocationsBounded()
    {
        // Baseline: ~17 MB — full sort must buffer all rows.
        (long bytes, int gen2) = await MeasureQueryAsync(
            "SELECT id, name, value FROM data ORDER BY value DESC",
            BuildCatalog());

        AssertAllocations("ORDER BY full", bytes, gen2, maxBytes: 35_000_000, maxGen2: 1);
    }

    // ──────────────── Distinct ────────────────

    [Fact]
    public async Task DistinctLowCardinality_AllocationsBounded()
    {
        // Baseline: ~3.4 MB
        (long bytes, int gen2) = await MeasureQueryAsync(
            "SELECT DISTINCT category FROM data",
            BuildCatalog());

        AssertAllocations("DISTINCT low-card", bytes, gen2, maxBytes: 8_000_000);
    }

    // ──────────────── Subqueries ────────────────

    [Fact]
    public async Task UncorrelatedInSubquery_AllocationsBounded()
    {
        // Baseline: ~4.0 MB
        (long bytes, int gen2) = await MeasureSubqueryAsync(
            "SELECT id, name FROM data WHERE id IN (SELECT lookup_id FROM lookup WHERE weight > 25)",
            BuildCatalog(withLookup: true));

        AssertAllocations("IN subquery", bytes, gen2, maxBytes: 10_000_000);
    }

    // ──────────────── Set Operations ────────────────

    [Fact]
    public async Task UnionAll_AllocationsBounded()
    {
        // Baseline: ~11.3 MB — two full scans concatenated.
        (long bytes, int gen2) = await MeasureQueryAsync(
            "SELECT id, name, value FROM data UNION ALL SELECT id, name, value FROM data_b",
            BuildCatalog(withSecond: true));

        AssertAllocations("UNION ALL", bytes, gen2, maxBytes: 24_000_000);
    }

    // ──────────────── CTE ────────────────

    [Fact]
    public async Task CteInlined_AllocationsBounded()
    {
        // Baseline: ~4.5 MB
        (long bytes, int gen2) = await MeasureQueryAsync(
            "WITH filtered AS (SELECT id, name, value FROM data WHERE value > 500) SELECT id, name FROM filtered",
            BuildCatalog());

        AssertAllocations("CTE inlined", bytes, gen2, maxBytes: 10_000_000);
    }

    // ──────────────── Distinct / Set-op composite-key paths ────────────────
    //
    // These tests target the per-row DataValue[] allocation in DistinctOperator
    // and SetOperationOperator when operating on multi-column (composite) keys.
    // The thresholds are calibrated BEFORE the scratch-buffer optimisation so
    // that the fix produces a measurable drop.

    [Fact]
    public async Task DistinctMultiColumn_AllocationsBounded()
    {
        // Exercises DistinctOperator lines 115 and 258 (composite key path).
        // Every row allocates new DataValue[columnCount] for the CompositeKey.
        (long bytes, int gen2) = await MeasureQueryAsync(
            "SELECT DISTINCT category, name FROM data",
            BuildCatalog());

        // Baseline: ~8.6 MB
        AssertAllocations("DISTINCT multi-col", bytes, gen2, maxBytes: 18_000_000);
    }

    [Fact]
    public async Task UnionDistinctMultiColumn_AllocationsBounded()
    {
        // Exercises SetOperationOperator.ExecuteUnionDistinctAsync composite key
        // path (lines 178, 301).
        (long bytes, int gen2) = await MeasureQueryAsync(
            "SELECT category, name FROM data UNION SELECT category, name FROM data_b",
            BuildCatalog(withSecond: true));

        // Baseline: ~15.5 MB
        AssertAllocations("UNION DISTINCT multi-col", bytes, gen2, maxBytes: 32_000_000);
    }

    [Fact]
    public async Task IntersectMultiColumn_AllocationsBounded()
    {
        // Exercises SetOperationOperator AddRowToSet and ContainsRow
        // composite key paths.
        (long bytes, int gen2) = await MeasureQueryAsync(
            "SELECT category, name FROM data INTERSECT SELECT category, name FROM data_b",
            BuildCatalog(withSecond: true));

        // Baseline: ~14.8 MB (down from ~16.2 MB before scratch-buffer optimisation)
        AssertAllocations("INTERSECT multi-col", bytes, gen2, maxBytes: 30_000_000);
    }

    [Fact]
    public async Task IntersectAllMultiColumn_AllocationsBounded()
    {
        // Exercises SetOperationOperator IncrementCount and DecrementCount
        // composite key paths.
        (long bytes, int gen2) = await MeasureQueryAsync(
            "SELECT category, name FROM data INTERSECT ALL SELECT category, name FROM data_b",
            BuildCatalog(withSecond: true));

        // Baseline: ~15.8 MB (down from ~17.3 MB before scratch-buffer optimisation)
        AssertAllocations("INTERSECT ALL multi-col", bytes, gen2, maxBytes: 32_000_000);
    }

    [Fact]
    public async Task ExceptMultiColumn_AllocationsBounded()
    {
        // Exercises SetOperationOperator AddRowToSet (line 1555) and
        // ContainsRow (line 1584) composite key paths.
        (long bytes, int gen2) = await MeasureQueryAsync(
            "SELECT category, name FROM data EXCEPT SELECT category, name FROM data_b",
            BuildCatalog(withSecond: true));

        // Baseline: ~16.4 MB (down from ~17.8 MB before scratch-buffer optimisation)
        AssertAllocations("EXCEPT multi-col", bytes, gen2, maxBytes: 34_000_000);
    }

    // ──────────────── GroupByOperator .ToArray() hot paths ────────────────

    [Fact]
    public async Task GroupByOrderedAggregate_AllocationsBounded()
    {
        // Exercises AccumulateRow line 1459: allArguments[i].ToArray() and
        // sortKeys.ToArray() per input row for ordered aggregates
        // (STRING_AGG(... ORDER BY ...)).
        // Before fix: ~X MB (per-row array allocation).
        // After fix: pooled arrays returned during flush.
        (long bytes, int gen2) = await MeasureQueryAsync(
            "SELECT category, STRING_AGG(name, ',' ORDER BY name) AS names FROM data GROUP BY category",
            BuildCatalog());

        // Baseline: ~8.7 MB — flat buffer eliminates 40K small per-row DataValue[]
        // allocations from .ToArray() (reduces GC object count, not total bytes).
        AssertAllocations("GROUP BY ordered agg", bytes, gen2, maxBytes: 18_000_000);
    }

    [Fact]
    public async Task GroupBySpillCompositeKey_AllocationsBounded()
    {
        // Exercises WriteSpillRow: compositeKeyScratch passed as ReadOnlySpan
        // instead of .ToArray(), and reaggregation uses scratch buffers
        // instead of per-row new DataValue[argCount].
        // Memory budget set very low to force spilling.
        (long bytes, int gen2) = await MeasureQueryAsync(
            "SELECT category, name, COUNT(*) AS cnt, AVG(value) AS avg_val FROM data GROUP BY category, name",
            BuildCatalog(),
            memoryBudgetBytes: 32 * 1024);

        // Baseline: ~26 MB (spill path dominates with serialization overhead).
        AssertAllocations("GROUP BY spill composite", bytes, gen2, maxBytes: 34_000_000, maxGen2: 1);
    }

    [Fact]
    public async Task GroupByStreamingSortedCompositeKey_AllocationsBounded()
    {
        // Exercises streaming sorted path line 258:
        // compositeKeyScratch.AsSpan(...).ToArray() per group boundary.
        // Uses ORDER BY on group keys to trigger the streaming sorted path.
        (long bytes, int gen2) = await MeasureQueryAsync(
            "SELECT category, COUNT(*) AS cnt FROM data GROUP BY category ORDER BY category",
            BuildCatalog());

        AssertAllocations("GROUP BY streaming sorted", bytes, gen2, maxBytes: 6_000_000);
    }

    // ──────────────── Assertion helper ────────────────

    private static void AssertAllocations(
        string label, long bytes, int gen2, long maxBytes, int maxGen2 = 0)
    {
        Assert.True(bytes <= maxBytes,
            $"{label} allocated {bytes:N0} bytes, exceeding the {maxBytes:N0} byte limit");
        Assert.True(gen2 <= maxGen2,
            $"{label} triggered {gen2} Gen2 collections (limit {maxGen2})");
    }

    // ──────────────── Measurement infrastructure ────────────────

    private static async Task<(long Bytes, int Gen2Collections)> MeasureQueryAsync(
        string sql, TableCatalog catalog, long? memoryBudgetBytes = null)
    {
        FunctionRegistry functions = FunctionRegistry.CreateDefault();

        // Warmup: JIT, ArrayPool priming, CSV schema inference cache.
        await RunQueryAsync(sql, catalog, functions, memoryBudgetBytes);

        return await MeasureCoreAsync(() => RunQueryAsync(sql, catalog, functions, memoryBudgetBytes));
    }

    private static async Task<(long Bytes, int Gen2Collections)> MeasureSubqueryAsync(
        string sql, TableCatalog catalog)
    {
        FunctionRegistry functions = FunctionRegistry.CreateDefault();

        // Warmup.
        await RunSubqueryAsync(sql, catalog, functions);

        return await MeasureCoreAsync(() => RunSubqueryAsync(sql, catalog, functions));
    }

    private static async Task<(long Bytes, int Gen2Collections)> MeasureCoreAsync(Func<Task> action)
    {
        // Force a clean GC state so prior test garbage doesn't trigger collections
        // during measurement.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);

        int gen2Before = GC.CollectionCount(2);
        long before = GC.GetAllocatedBytesForCurrentThread();

        await action();

        long after = GC.GetAllocatedBytesForCurrentThread();
        int gen2After = GC.CollectionCount(2);

        return (after - before, gen2After - gen2Before);
    }

    private static async Task RunQueryAsync(
        string sql, TableCatalog catalog, FunctionRegistry functions, long? memoryBudgetBytes = null)
    {
        QueryExpression query = SqlParser.Parse(sql);
        QueryPlanner planner = new(catalog, functions);
        IQueryOperator root = planner.Plan(query);
        using LocalBufferPool pool = new();
        ExecutionContext context = new(CancellationToken.None, functions, catalog, pool, memoryBudgetBytes: memoryBudgetBytes);

        await foreach (RowBatch batch in root.ExecuteAsync(context))
        {
            batch.Return();
        }
    }

    private static async Task RunSubqueryAsync(
        string sql, TableCatalog catalog, FunctionRegistry functions)
    {
        QueryExpression query = SqlParser.Parse(sql);
        QueryPlanner planner = new(catalog, functions);
        using LocalBufferPool pool = new();
        ExecutionContext context = new(CancellationToken.None, functions, catalog, pool);
        IQueryOperator root = await planner.PlanWithSubqueriesAsync(query, context, CancellationToken.None);

        await foreach (RowBatch batch in root.ExecuteAsync(context))
        {
            batch.Return();
        }
    }

    // ──────────────── Test data setup ────────────────

    private TableCatalog BuildCatalog(bool withLookup = false, bool withSecond = false, bool withCompositeKeyLookup = false)
    {
        TableCatalog catalog = new();
        catalog.Register(new TableDescriptor("csv", "data", _csvPath, new Dictionary<string, string>()));

        if (withLookup)
        {
            catalog.Register(new TableDescriptor("csv", "lookup", _lookupCsvPath, new Dictionary<string, string>()));
        }

        if (withCompositeKeyLookup)
        {
            catalog.Register(new TableDescriptor("csv", "composite_lookup", _compositeKeyLookupCsvPath, new Dictionary<string, string>()));
        }

        if (withSecond)
        {
            catalog.Register(new TableDescriptor("csv", "data_b", _secondCsvPath, new Dictionary<string, string>()));
        }

        return catalog;
    }

    private string GenerateCsv(string fileName, int rowCount, int seed)
    {
        string filePath = Path.Combine(_tempDir, fileName);
        using StreamWriter writer = new(filePath);
        writer.WriteLine("id,name,value,category,score");

        Random random = new(seed);
        string[] categories = ["alpha", "beta", "gamma", "delta", "epsilon"];

        for (int i = 0; i < rowCount; i++)
        {
            string name = $"item_{i:D6}";
            float value = (float)(random.NextDouble() * 1000.0);
            string category = categories[random.Next(categories.Length)];
            float score = (float)(random.NextDouble() * 100.0);
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{i},{name},{value:F4},{category},{score:F4}"));
        }

        return filePath;
    }

    private string GenerateLookupCsv(string fileName, int rowCount, int seed)
    {
        string filePath = Path.Combine(_tempDir, fileName);
        using StreamWriter writer = new(filePath);
        writer.WriteLine("lookup_id,description,weight");

        Random random = new(seed);

        for (int i = 0; i < rowCount; i++)
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{i},desc_{i:D6},{(float)(random.NextDouble() * 50.0):F4}"));
        }

        return filePath;
    }

    private string GenerateCompositeKeyLookupCsv(string fileName, int rowCount, int seed)
    {
        string filePath = Path.Combine(_tempDir, fileName);
        using StreamWriter writer = new(filePath);
        writer.WriteLine("lookup_id,category,description");

        Random random = new(seed);
        string[] categories = ["alpha", "beta", "gamma", "delta", "epsilon"];

        for (int i = 0; i < rowCount; i++)
        {
            string category = categories[random.Next(categories.Length)];
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{i},{category},desc_{i:D6}"));
        }

        return filePath;
    }
}
