using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.DatumFile.V2;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Catalog.Providers;

/// <summary>
/// Verifies three-tier zone-map pruning in <see cref="DatumFileTableProviderV2"/>.
/// Each test writes a multi-page v2 file with known integer ranges per
/// page, then issues a filter and asserts the scan yields only the rows
/// from pages that survive pruning.
/// </summary>
public sealed class DatumFileTableProviderV2PruningTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"v2_pruning_{Guid.NewGuid():N}");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task NoFilter_ReadsAllRows()
    {
        string path = WriteSequentialIntFile("nofilter.datum", rowCount: 100, pageSize: 32);

        using TableCatalog catalog = new(new Pool(new PoolBacking()));
        ITableProvider provider = catalog.Add(new TableDescriptor("t", path));
        Assert.IsType<DatumFileTableProviderV2>(provider);

        int rowsRead = await CountRowsAsync(provider, filterHint: null);
        Assert.Equal(100, rowsRead);
    }

    [Fact]
    public async Task RangeFilter_PrunesPagesOutsideRange()
    {
        // 100 rows, pageSize 32 → 4 pages with id ranges:
        //   page 0: [0, 31]
        //   page 1: [32, 63]
        //   page 2: [64, 95]
        //   page 3: [96, 99]
        // Filter id < 50: pages 2 and 3 prune (their min > 50). Pages 0
        // and 1 survive — scan yields 64 rows (all of pages 0+1, the
        // engine drops 32..49 at row-level — that's not our concern here).
        string path = WriteSequentialIntFile("range.datum", rowCount: 100, pageSize: 32);

        Expression filter = new BinaryExpression(
            new ColumnReference("id"),
            BinaryOperator.LessThan,
            new LiteralExpression(50L));

        using TableCatalog catalog = new(new Pool(new PoolBacking()));
        ITableProvider provider = catalog.Add(new TableDescriptor("t", path));

        int rowsRead = await CountRowsAsync(provider, filterHint: filter);

        // 64 = page 0 (32) + page 1 (32). Pages 2 (32) + 3 (4) pruned.
        Assert.Equal(64, rowsRead);
    }

    [Fact]
    public async Task FilterMatchingNoPages_PrunesEverything()
    {
        string path = WriteSequentialIntFile("nomatch.datum", rowCount: 100, pageSize: 32);

        // id < 0: every page's min is ≥ 0, so every page prunes.
        Expression filter = new BinaryExpression(
            new ColumnReference("id"),
            BinaryOperator.LessThan,
            new LiteralExpression(0L));

        using TableCatalog catalog = new(new Pool(new PoolBacking()));
        ITableProvider provider = catalog.Add(new TableDescriptor("t", path));

        int rowsRead = await CountRowsAsync(provider, filterHint: filter);
        Assert.Equal(0, rowsRead);
    }

    [Fact]
    public async Task FilterAboveAllPages_PrunesEverything()
    {
        string path = WriteSequentialIntFile("above.datum", rowCount: 100, pageSize: 32);

        // id > 1000: every page's max is ≤ 99, so every page prunes.
        Expression filter = new BinaryExpression(
            new ColumnReference("id"),
            BinaryOperator.GreaterThan,
            new LiteralExpression(1000L));

        using TableCatalog catalog = new(new Pool(new PoolBacking()));
        ITableProvider provider = catalog.Add(new TableDescriptor("t", path));

        int rowsRead = await CountRowsAsync(provider, filterHint: filter);
        Assert.Equal(0, rowsRead);
    }

    [Fact]
    public async Task EqualityFilter_KeepsOnlyMatchingPage()
    {
        string path = WriteSequentialIntFile("eq.datum", rowCount: 100, pageSize: 32);

        // id = 70: only page 2 (range [64, 95]) survives. 32 rows yielded.
        Expression filter = new BinaryExpression(
            new ColumnReference("id"),
            BinaryOperator.Equal,
            new LiteralExpression(70L));

        using TableCatalog catalog = new(new Pool(new PoolBacking()));
        ITableProvider provider = catalog.Add(new TableDescriptor("t", path));

        int rowsRead = await CountRowsAsync(provider, filterHint: filter);
        Assert.Equal(32, rowsRead);
    }

    [Fact]
    public async Task FilterOnUnknownColumn_DoesNotPrune()
    {
        // Filter references a column not in the schema — the predicate
        // evaluator can't derive any bound, so no pruning happens and
        // every page is read.
        string path = WriteSequentialIntFile("unknown.datum", rowCount: 100, pageSize: 32);

        Expression filter = new BinaryExpression(
            new ColumnReference("nonexistent"),
            BinaryOperator.Equal,
            new LiteralExpression(42L));

        using TableCatalog catalog = new(new Pool(new PoolBacking()));
        ITableProvider provider = catalog.Add(new TableDescriptor("t", path));

        int rowsRead = await CountRowsAsync(provider, filterHint: filter);
        Assert.Equal(100, rowsRead);
    }

    // ──────────────────── Helpers ────────────────────

    /// <summary>
    /// Writes a single-column ("id", Int64, non-nullable) v2 .datum file
    /// with values 0..rowCount-1. Uses the test page size so multi-page
    /// pruning is exercised even on small fixtures.
    /// </summary>
    private string WriteSequentialIntFile(string fileName, int rowCount, int pageSize)
    {
        string path = Path.Combine(_tempDir, fileName);
        ColumnDescriptorV2 column = new("id", DataKind.Int64, EncoderKind.FixedWidth, IsNullable: false);

        Pool pool = new(new PoolBacking());
        ColumnLookup lookup = new(["id"]);
        Arena arena = new();
        RowBatch batch = pool.RentRowBatch(lookup, capacity: rowCount, arena: arena);
        for (int i = 0; i < rowCount; i++)
        {
            DataValue[] row = pool.RentDataValues(1);
            row[0] = DataValue.FromInt64(i);
            batch.Add(row);
        }

        using DatumFileWriterV2 writer = new(path, sidecarPath: null);
        writer.SetPageSize(pageSize);
        writer.Initialize([column]);
        writer.WriteRowBatch(batch);
        writer.FinalizeWriter();

        return path;
    }

    private static async Task<int> CountRowsAsync(ITableProvider provider, Expression? filterHint)
    {
        int total = 0;
        await foreach (RowBatch batch in provider.ScanAsync(
            requiredColumns: null,
            filterHint: filterHint,
            cancellationToken: default))
        {
            total += batch.Count;
        }
        return total;
    }
}
