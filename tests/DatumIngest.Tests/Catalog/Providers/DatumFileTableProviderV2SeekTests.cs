using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.DatumFile.V2;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Catalog.Providers;

/// <summary>
/// Verifies <see cref="DatumFileTableProviderV2.OpenSeekSession"/> +
/// <see cref="ISeekSession.SeekAsync"/> on multi-page v2 files. Covers
/// single-row seeks, ranges within one page, ranges spanning pages, and
/// boundary cases (clamped end, beyond EOF, empty count).
/// </summary>
public sealed class DatumFileTableProviderV2SeekTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"v2_seek_{Guid.NewGuid():N}");

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
    public async Task SeekSingleRow_ReturnsExactRow()
    {
        // 100 rows of values 0..99 with pageSize 32 → 4 pages.
        string path = WriteSequentialFile("single.datum", rowCount: 100, pageSize: 32);

        using TableCatalog catalog = new(new Pool(new PoolBacking()));
        ITableProvider provider = catalog.Add(new TableDescriptor("t", path));
        Assert.True(provider.Seekable);

        using ISeekSession session = provider.OpenSeekSession(requiredColumns: null);

        // Row 50 is on page 1 (page 1 covers rows 32-63), at position 18 in that page.
        List<long> values = await CollectAsync(session.SeekAsync(50, 1, default));
        Assert.Single(values);
        Assert.Equal(50L, values[0]);
    }

    [Fact]
    public async Task SeekWithinPage_ReturnsRange()
    {
        string path = WriteSequentialFile("within.datum", rowCount: 100, pageSize: 32);

        using TableCatalog catalog = new(new Pool(new PoolBacking()));
        ITableProvider provider = catalog.Add(new TableDescriptor("t", path));
        using ISeekSession session = provider.OpenSeekSession(requiredColumns: null);

        // Rows 5-14 (10 rows) all live in page 0 (covers rows 0-31).
        List<long> values = await CollectAsync(session.SeekAsync(5, 10, default));
        Assert.Equal(Enumerable.Range(5, 10).Select(i => (long)i), values);
    }

    [Fact]
    public async Task SeekSpansPages_StitchesCorrectly()
    {
        string path = WriteSequentialFile("span.datum", rowCount: 100, pageSize: 32);

        using TableCatalog catalog = new(new Pool(new PoolBacking()));
        ITableProvider provider = catalog.Add(new TableDescriptor("t", path));
        using ISeekSession session = provider.OpenSeekSession(requiredColumns: null);

        // Rows 28-67 (40 rows) span pages 0, 1, and 2.
        List<long> values = await CollectAsync(session.SeekAsync(28, 40, default));
        Assert.Equal(Enumerable.Range(28, 40).Select(i => (long)i), values);
    }

    [Fact]
    public async Task SeekClampsToEndOfFile()
    {
        string path = WriteSequentialFile("clamp.datum", rowCount: 100, pageSize: 32);

        using TableCatalog catalog = new(new Pool(new PoolBacking()));
        ITableProvider provider = catalog.Add(new TableDescriptor("t", path));
        using ISeekSession session = provider.OpenSeekSession(requiredColumns: null);

        // Asking for 50 rows starting at 80 — file only has 20 rows left
        // (rows 80-99). Session should clamp and yield those 20 rows.
        List<long> values = await CollectAsync(session.SeekAsync(80, 50, default));
        Assert.Equal(20, values.Count);
        Assert.Equal(80L, values[0]);
        Assert.Equal(99L, values[^1]);
    }

    [Fact]
    public async Task SeekBeyondEndOfFile_ReturnsNothing()
    {
        string path = WriteSequentialFile("eof.datum", rowCount: 100, pageSize: 32);

        using TableCatalog catalog = new(new Pool(new PoolBacking()));
        ITableProvider provider = catalog.Add(new TableDescriptor("t", path));
        using ISeekSession session = provider.OpenSeekSession(requiredColumns: null);

        List<long> values = await CollectAsync(session.SeekAsync(200, 10, default));
        Assert.Empty(values);
    }

    [Fact]
    public async Task SeekZeroCount_ReturnsNothing()
    {
        string path = WriteSequentialFile("zero.datum", rowCount: 100, pageSize: 32);

        using TableCatalog catalog = new(new Pool(new PoolBacking()));
        ITableProvider provider = catalog.Add(new TableDescriptor("t", path));
        using ISeekSession session = provider.OpenSeekSession(requiredColumns: null);

        List<long> values = await CollectAsync(session.SeekAsync(50, 0, default));
        Assert.Empty(values);
    }

    [Fact]
    public async Task RepeatedSeeks_ProduceCorrectRows()
    {
        // Mirrors ScanOperator's exact-seek loop: open one session, issue
        // many count=1 SeekAsync calls in sorted order. Each must return
        // the requested row.
        string path = WriteSequentialFile("repeat.datum", rowCount: 100, pageSize: 32);

        using TableCatalog catalog = new(new Pool(new PoolBacking()));
        ITableProvider provider = catalog.Add(new TableDescriptor("t", path));
        using ISeekSession session = provider.OpenSeekSession(requiredColumns: null);

        long[] positions = [3, 17, 32, 33, 64, 95, 99];
        List<long> collected = new();
        foreach (long pos in positions)
        {
            collected.AddRange(await CollectAsync(session.SeekAsync(pos, 1, default)));
        }
        Assert.Equal(positions, collected);
    }

    [Fact]
    public async Task ConcurrentSessions_AreIndependent()
    {
        // Two sessions on the same provider seek different rows
        // simultaneously. Each session owns its own reader so they don't
        // race on FileStream.Position.
        string path = WriteSequentialFile("concurrent.datum", rowCount: 100, pageSize: 32);

        using TableCatalog catalog = new(new Pool(new PoolBacking()));
        ITableProvider provider = catalog.Add(new TableDescriptor("t", path));
        using ISeekSession a = provider.OpenSeekSession(requiredColumns: null);
        using ISeekSession b = provider.OpenSeekSession(requiredColumns: null);

        Task<List<long>> aTask = CollectAsync(a.SeekAsync(0, 50, default));
        Task<List<long>> bTask = CollectAsync(b.SeekAsync(50, 50, default));
        List<long>[] results = await Task.WhenAll(aTask, bTask);

        Assert.Equal(Enumerable.Range(0, 50).Select(i => (long)i), results[0]);
        Assert.Equal(Enumerable.Range(50, 50).Select(i => (long)i), results[1]);
    }

    // ──────────────────── Helpers ────────────────────

    private string WriteSequentialFile(string fileName, int rowCount, int pageSize)
    {
        string path = Path.Combine(_tempDir, fileName);
        ColumnDescriptorV2 column = new("id", DataKind.Int64, EncoderKind.FixedWidth, IsNullable: false);

        Pool pool = CreatePool();
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

    private static async Task<List<long>> CollectAsync(IAsyncEnumerable<RowBatch> batches)
    {
        List<long> result = new();
        await foreach (RowBatch batch in batches)
        {
            for (int i = 0; i < batch.Count; i++)
            {
                result.Add(batch[i][0].AsInt64());
            }
        }
        return result;
    }
}
