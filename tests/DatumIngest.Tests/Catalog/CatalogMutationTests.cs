using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.DatumFile.V2;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// PR8 tests for ALTER TABLE / INSERT / DELETE through the
/// <see cref="ITableProvider"/> mutation surface, dispatched via
/// the catalog's table indexer. Covers
/// <see cref="ITableProvider.AddColumn"/> / <see cref="ITableProvider.DropColumn"/> /
/// <see cref="ITableProvider.AppendRowsAsync"/> / <see cref="ITableProvider.DeleteRows"/>
/// against both <see cref="InMemoryTableProvider"/> and
/// <see cref="DatumFileTableProviderV2"/>, plus the read-only
/// rejection for system tables and the snapshot-readers semantics
/// that keeps in-flight scans alive across a mutation.
/// </summary>
public sealed class CatalogMutationTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_pr8_{Guid.NewGuid():N}");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        return Task.CompletedTask;
    }

    // ──────────────────── In-memory provider, catalog dispatch ────────────────────

    [Fact]
    public void Catalog_AddColumn_InMemory_AppendsNullableColumnAndBackfillsExistingRows()
    {
        Pool pool = CreatePool();
        TableCatalog catalog = CreateCatalog(pool);
        InMemoryTableProvider provider = new(pool, "t",
            columns: ["a", "b"],
            rows: [[1, 10], [2, 20]]);
        catalog.Add(provider);

        catalog["t"].AddColumn(new ColumnInfo("c", DataKind.Int32, nullable: true));

        Schema schema = provider.GetSchema();
        Assert.Equal(["a", "b", "c"], schema.Columns.Select(c => c.Name));
        Assert.Equal(2, provider.GetRowCount());

        // Sanity: rows visible via scan, c is null.
        long nullCount = 0;
        long total = 0;
        foreach (RowBatch batch in DrainScanSync(provider))
        {
            total += batch.Count;
            for (int r = 0; r < batch.Count; r++)
            {
                if (batch[r][2].IsNull) nullCount++;
            }
        }
        Assert.Equal(2, total);
        Assert.Equal(2, nullCount);
    }

    [Fact]
    public void Catalog_AddColumn_NotNullableColumn_Throws()
    {
        Pool pool = CreatePool();
        TableCatalog catalog = CreateCatalog(pool);
        catalog.Add(new InMemoryTableProvider(pool, "t", columns: ["a"], rows: [[1], [2]]));

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            catalog["t"].AddColumn(new ColumnInfo("nope", DataKind.Int32, nullable: false)));
        Assert.Contains("nullable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Catalog_DropColumn_InMemory_RemovesFromSchemaAndRows()
    {
        Pool pool = CreatePool();
        TableCatalog catalog = CreateCatalog(pool);
        InMemoryTableProvider provider = new(pool, "t",
            columns: ["a", "b", "c"],
            rows: [[1, 10, 100], [2, 20, 200]]);
        catalog.Add(provider);

        catalog["t"].DropColumn("b");

        Schema schema = provider.GetSchema();
        Assert.Equal(["a", "c"], schema.Columns.Select(c => c.Name));

        // Scanned values reflect the drop.
        List<int> aValues = new();
        List<int> cValues = new();
        foreach (RowBatch batch in DrainScanSync(provider))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                aValues.Add(batch[r][0].AsInt32());
                cValues.Add(batch[r][1].AsInt32());
            }
        }
        Assert.Equal([1, 2], aValues);
        Assert.Equal([100, 200], cValues);
    }

    [Fact]
    public async Task Catalog_AppendRowsAsync_InMemory_GrowsRowCountAndScanReturnsAppended()
    {
        Pool pool = CreatePool();
        TableCatalog catalog = CreateCatalog(pool);
        InMemoryTableProvider provider = new(pool, "t",
            columns: ["a", "b"],
            rows: [[1, "one"]]);
        catalog.Add(provider);

        // Build a batch matching the schema and feed it through the catalog.
        ColumnLookup lookup = new(["a", "b"]);
        Arena arena = CreateArena();
        RowBatch batch = pool.RentRowBatch(lookup, capacity: 2, arena: arena);
        DataValue[] row1 = pool.RentDataValues(2);
        row1[0] = DataValue.FromInt32(2); row1[1] = DataValue.FromString("two", arena);
        batch.Add(row1);
        DataValue[] row2 = pool.RentDataValues(2);
        row2[0] = DataValue.FromInt32(3); row2[1] = DataValue.FromString("three", arena);
        batch.Add(row2);

        await catalog["t"].AppendRowsAsync(AsyncEnumerableOf(batch), CancellationToken.None);

        Assert.Equal(3, provider.GetRowCount());

        List<string> names = new();
        foreach (RowBatch b in DrainScanSync(provider))
        {
            for (int r = 0; r < b.Count; r++)
            {
                names.Add(b[r][1].AsString(b.Arena));
            }
        }
        Assert.Equal(["one", "two", "three"], names);
    }

    [Fact]
    public void Catalog_DeleteRows_InMemory_SkipsDeletedRows()
    {
        Pool pool = CreatePool();
        TableCatalog catalog = CreateCatalog(pool);
        InMemoryTableProvider provider = new(pool, "t",
            columns: ["a"],
            rows: [[1], [2], [3], [4], [5]]);
        catalog.Add(provider);

        catalog["t"].DeleteRows([1L, 3L]); // delete rows 2 and 4

        Assert.Equal(3, provider.GetRowCount());
        List<int> aValues = new();
        foreach (RowBatch b in DrainScanSync(provider))
        {
            for (int r = 0; r < b.Count; r++)
            {
                aValues.Add(b[r][0].AsInt32());
            }
        }
        Assert.Equal([1, 3, 5], aValues);
    }

    // ──────────────────── System table read-only rejection ────────────────────

    [Fact]
    public void Catalog_AddColumn_OnSystemTable_ThrowsNotSupported()
    {
        // information_schema.tables is auto-registered by the catalog
        // and must be read-only — its provider doesn't override the
        // capability flags so the default ITableProvider.AddColumn
        // throws NotSupportedException.
        Pool pool = CreatePool();
        TableCatalog catalog = CreateCatalog(pool);

        NotSupportedException ex = Assert.Throws<NotSupportedException>(() =>
            catalog["information_schema.tables"].AddColumn(
                new ColumnInfo("nope", DataKind.Int32, nullable: true)));
        Assert.Contains("AddColumn", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Catalog_DropColumn_OnUnknownTable_ThrowsKeyNotFound()
    {
        Pool pool = CreatePool();
        TableCatalog catalog = CreateCatalog(pool);
        Assert.Throws<KeyNotFoundException>(() => catalog["nope"].DropColumn("x"));
    }

    // ──────────────────── Datum file provider, catalog dispatch ────────────────────

    [Fact]
    public void Catalog_AddColumn_DatumFile_RoundTripsThroughProvider()
    {
        string path = WriteSimpleDatumFile("add_col.datum");
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.AddFile(path, name: "t");

        catalog["t"].AddColumn(new ColumnInfo("note", DataKind.String, nullable: true));

        // Provider's snapshot has been swapped — schema reflects the new column.
        Schema schema = catalog["t"].GetSchema();
        Assert.Equal(["a", "b", "note"], schema.Columns.Select(c => c.Name));
        Assert.Equal(3, catalog["t"].GetRowCount());

        // The new column is null for every existing row.
        int nullCount = 0;
        foreach (RowBatch batch in DrainScanSync(catalog["t"]))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                if (batch[r][2].IsNull) nullCount++;
            }
        }
        Assert.Equal(3, nullCount);
    }

    [Fact]
    public void Catalog_DropColumn_DatumFile_RemovesFromSchema()
    {
        string path = WriteSimpleDatumFile("drop_col.datum");
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.AddFile(path, name: "t");

        catalog["t"].DropColumn("b");

        Schema schema = catalog["t"].GetSchema();
        Assert.Equal(["a"], schema.Columns.Select(c => c.Name));
    }

    [Fact]
    public async Task Catalog_AppendRowsAsync_DatumFile_GrowsRowCount()
    {
        string path = WriteSimpleDatumFile("append.datum");
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.AddFile(path, name: "t");

        long beforeCount = catalog["t"].GetRowCount();

        ColumnLookup lookup = new(["a", "b"]);
        Arena arena = CreateArena();
        RowBatch batch = pool.RentRowBatch(lookup, capacity: 2, arena: arena);
        DataValue[] row = pool.RentDataValues(2);
        row[0] = DataValue.FromInt32(99); row[1] = DataValue.FromInt32(990);
        batch.Add(row);

        await catalog["t"].AppendRowsAsync(AsyncEnumerableOf(batch), CancellationToken.None);

        Assert.Equal(beforeCount + 1, catalog["t"].GetRowCount());

        // Scan reflects the new row (last row's a value should be 99).
        List<int> aValues = new();
        foreach (RowBatch b in DrainScanSync(catalog["t"]))
        {
            for (int r = 0; r < b.Count; r++) aValues.Add(b[r][0].AsInt32());
        }
        Assert.Contains(99, aValues);
    }

    [Fact]
    public void Catalog_DeleteRows_DatumFile_SkipsDeletedRows()
    {
        string path = WriteSimpleDatumFile("delete.datum");
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.AddFile(path, name: "t");

        catalog["t"].DeleteRows([1L]); // soft-delete middle row

        // Scan no longer surfaces row index 1.
        List<int> aValues = new();
        foreach (RowBatch b in DrainScanSync(catalog["t"]))
        {
            for (int r = 0; r < b.Count; r++) aValues.Add(b[r][0].AsInt32());
        }
        Assert.Equal([0, 2], aValues);
    }

    // ──────────────────── Snapshot / concurrency ────────────────────

    [Fact]
    public async Task DatumProvider_AddColumn_DuringInFlightScan_OldScanCompletesAgainstOldSchema()
    {
        // Snapshot semantics: capture the scan iterator before mutation,
        // then mutate, then drain the iterator. The captured scan must
        // continue to see the pre-mutation schema width because its
        // snapshot refcount keeps the old reader alive.
        string path = WriteSimpleDatumFile("concurrent.datum");
        Pool pool = CreatePool();
        using DatumFileTableProviderV2 provider = new(new TableDescriptor("t", path), pool);

        IAsyncEnumerator<RowBatch> scan = provider.ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null,
            cancellationToken: default).GetAsyncEnumerator();

        // Pull the first batch — provider has captured the snapshot.
        Assert.True(await scan.MoveNextAsync());
        int oldWidth = scan.Current.ColumnLookup.Count;
        Assert.Equal(2, oldWidth);

        // Mutate while the scan is mid-iteration.
        provider.AddColumn(new ColumnInfo("c", DataKind.Int32, nullable: true));

        // Continue draining — old scan still emits 2-column batches.
        while (await scan.MoveNextAsync())
        {
            Assert.Equal(2, scan.Current.ColumnLookup.Count);
        }
        await scan.DisposeAsync();

        // A fresh scan after the mutation sees three columns.
        IAsyncEnumerator<RowBatch> newScan = provider.ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null,
            cancellationToken: default).GetAsyncEnumerator();
        Assert.True(await newScan.MoveNextAsync());
        Assert.Equal(3, newScan.Current.ColumnLookup.Count);
        while (await newScan.MoveNextAsync()) { }
        await newScan.DisposeAsync();
    }

    [Fact]
    public void SidecarRegistry_UpdateAt_ReplacesRegisteredSource()
    {
        // Direct unit test for the registry helper PR8 added.
        SidecarRegistry registry = new();
        FakeBlobSource s1 = new("v1");
        FakeBlobSource s2 = new("v2");

        byte id = registry.Register(s1);
        Assert.Same(s1, registry.Resolve(id));

        registry.UpdateAt(id, s2);
        Assert.Same(s2, registry.Resolve(id));
    }

    [Fact]
    public void SidecarRegistry_UpdateAt_OnUnregisteredSlot_Throws()
    {
        SidecarRegistry registry = new();
        // Slot 0 is unregistered.
        Assert.Throws<InvalidOperationException>(() =>
            registry.UpdateAt(0, new FakeBlobSource("v")));
    }

    // ──────────────────── Helpers ────────────────────

    private static IEnumerable<RowBatch> DrainScanSync(ITableProvider provider)
    {
        // Helper that drains the async enumerable into a list synchronously
        // for tests that don't otherwise need async.
        IAsyncEnumerator<RowBatch> e = provider.ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null,
            cancellationToken: default).GetAsyncEnumerator();
        try
        {
            while (e.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                yield return e.Current;
            }
        }
        finally
        {
            e.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static async IAsyncEnumerable<RowBatch> AsyncEnumerableOf(params RowBatch[] batches)
    {
        foreach (RowBatch b in batches)
        {
            yield return b;
        }
        await Task.CompletedTask;
    }

    private string WriteSimpleDatumFile(string fileName)
    {
        // Two-column, three-row .datum file: a (Int32), b (Int32).
        ColumnDescriptorV2 colA = new("a", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        ColumnDescriptorV2 colB = new("b", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);

        Pool pool = CreatePool();
        ColumnLookup lookup = new(["a", "b"]);
        Arena arena = CreateArena();
        RowBatch batch = pool.RentRowBatch(lookup, capacity: 3, arena: arena);
        for (int i = 0; i < 3; i++)
        {
            DataValue[] row = pool.RentDataValues(2);
            row[0] = DataValue.FromInt32(i);
            row[1] = DataValue.FromInt32(i * 10);
            batch.Add(row);
        }

        string path = Path.Combine(_tempDir, fileName);
        using (DatumFileWriterV2 writer = new(path, sidecarPath: null))
        {
            writer.Initialize([colA, colB]);
            writer.WriteRowBatch(batch);
            writer.FinalizeWriter();
        }
        return path;
    }

    private sealed class FakeBlobSource : IBlobSource
    {
        private readonly string _tag;
        public FakeBlobSource(string tag) { _tag = tag; }
        public ReadOnlySpan<byte> Read(long offset, long length) => ReadOnlySpan<byte>.Empty;
        public void Dispose() { }
        public override string ToString() => _tag;
    }
}
