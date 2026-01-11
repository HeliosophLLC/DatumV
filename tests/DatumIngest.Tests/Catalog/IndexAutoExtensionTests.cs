using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Indexing;
using DatumIngest.Ingestion;
using DatumIngest.Model;
using DatumIngest.Pooling;
using DatumIngest.Serialization;
using DatumIngest.Serialization.Csv;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// PR13a-2 tests for the two-phase commit that auto-refreshes
/// <c>.datum-index</c> after every INSERT. The unlock: users no
/// longer need to run a manual <c>REINDEX</c> after data mutations
/// to restore acceleration. The failure-mode contract: if the index
/// refresh fails for any reason, the data commit stands and the
/// index surfaces as <see cref="IndexValidity.Stale"/> via
/// <c>datum_catalog.indexes.is_valid = false</c>.
/// </summary>
public sealed class IndexAutoExtensionTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_pr13a2_{Guid.NewGuid():N}");

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
    public async Task Append_AutoRefreshesIndex_WithoutManualReindex()
    {
        // The PR13a-2 unlock: an INSERT no longer leaves the index
        // Stale until the user runs REINDEX. The two-phase commit
        // refreshes .datum-index in the same session, so the next
        // GetSourceIndex call returns a valid snapshot.
        string datumPath = await IngestAndIndex("auto_extend.datum");

        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        ITableProvider provider = catalog.Add(new TableDescriptor("t", datumPath));

        Assert.NotNull(provider.GetSourceIndex());
        Assert.Equal(IndexValidity.Valid, provider.GetIndexValidity());

        Schema schema = provider.GetSchema();
        await catalog.AppendRowsAsync("t",
            MakeBatchesMatchingSchema(pool, schema, [[5, "extra"]]),
            CancellationToken.None);

        // Without PR13a-2 this would be null (Stale) and require REINDEX.
        Assert.NotNull(provider.GetSourceIndex());
        Assert.Equal(IndexValidity.Valid, provider.GetIndexValidity());
    }

    [Fact]
    public async Task Append_NoExistingIndex_StillCreatesOneOnFirstInsert()
    {
        // CREATE TABLE makes a .datum but no .datum-index. The first
        // INSERT's two-phase commit creates the index from scratch.
        // After this, indexed queries get acceleration without the
        // user ever running REINDEX manually.
        Pool pool = new(new PoolBacking());
        string catalogPath = Path.Combine(_tempDir, ".datum-catalog.json");
        using TableCatalog catalog = new(pool, catalogPath);

        catalog.Plan("CREATE TABLE t (id Int32, name String)");
        ITableProvider provider = catalog["t"];

        Assert.Equal(IndexValidity.Missing, provider.GetIndexValidity());

        catalog.Plan("INSERT INTO t VALUES (1, 'a'), (2, 'b')");

        Assert.Equal(IndexValidity.Valid, provider.GetIndexValidity());
        Assert.NotNull(provider.GetSourceIndex());

        string indexPath = Path.ChangeExtension(Path.Combine(_tempDir, "t.datum"), ".datum-index");
        Assert.True(File.Exists(indexPath), "first INSERT should have produced the .datum-index");
    }

    [Fact]
    public async Task IsValid_ColumnSurfaces_StaleSentinelRow()
    {
        // After a forced manual invalidation, the index appears in
        // datum_catalog.indexes with is_valid = false so users can
        // spot the table that needs REINDEX.
        string datumPath = await IngestAndIndex("stale_sentinel.datum");

        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        ITableProvider provider = catalog.Add(new TableDescriptor("t", datumPath));

        // Force the in-memory cache invalid by simulating a torn write —
        // overwrite the trailing IDXT magic on the file. Reopening the
        // provider via a fresh catalog will see the file as invalid.
        Assert.NotNull(provider.GetSourceIndex());
        catalog.Dispose();

        string indexPath = Path.ChangeExtension(datumPath, ".datum-index");
        long fileLength = new FileInfo(indexPath).Length;
        using (FileStream fs = File.Open(indexPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            fs.Position = fileLength - 4;
            fs.Write(new byte[] { 0, 0, 0, 0 });
        }

        // Reopen — the provider's TryLoadSourceIndex sees the torn tail,
        // returns null, but the file still exists → IndexValidity.Stale.
        Pool pool2 = new(new PoolBacking());
        using TableCatalog reopened = new(pool2);
        ITableProvider reopenedProvider = reopened.Add(new TableDescriptor("t", datumPath));

        Assert.Equal(IndexValidity.Stale, reopenedProvider.GetIndexValidity());
        Assert.Null(reopenedProvider.GetSourceIndex());

        // Query datum_catalog.indexes — table 't' should appear with
        // is_valid = false.
        bool sawStaleRow = false;
        await foreach (RowBatch batch in reopened["datum_catalog.indexes"]
            .ScanAsync(null, null, null, CancellationToken.None))
        {
            try
            {
                Arena arena = batch.Arena;
                for (int r = 0; r < batch.Count; r++)
                {
                    Row row = batch[r];
                    string tableName = row[0].AsString(arena);
                    bool isValid = row[6].AsBoolean();
                    if (string.Equals(tableName, "t", StringComparison.OrdinalIgnoreCase) && !isValid)
                    {
                        sawStaleRow = true;
                    }
                }
            }
            finally { batch.Dispose(); }
        }
        Assert.True(sawStaleRow, "stale table 't' should surface in datum_catalog.indexes with is_valid = false");
    }

    [Fact]
    public async Task IsValid_ColumnSurfaces_TrueAfterAutoRefresh()
    {
        // After an INSERT, the index is auto-refreshed; the column
        // entries appear with is_valid = true.
        string datumPath = await IngestAndIndex("valid_after_append.datum");

        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        ITableProvider provider = catalog.Add(new TableDescriptor("t", datumPath));

        Schema schema = provider.GetSchema();
        await catalog.AppendRowsAsync("t",
            MakeBatchesMatchingSchema(pool, schema, [[7, "rev"]]),
            CancellationToken.None);

        // Find the row(s) in datum_catalog.indexes for table 't'. Every
        // entry should have is_valid = true.
        int rowsForT = 0;
        await foreach (RowBatch batch in catalog["datum_catalog.indexes"]
            .ScanAsync(null, null, null, CancellationToken.None))
        {
            try
            {
                Arena arena = batch.Arena;
                for (int r = 0; r < batch.Count; r++)
                {
                    Row row = batch[r];
                    if (string.Equals(row[0].AsString(arena), "t", StringComparison.OrdinalIgnoreCase))
                    {
                        rowsForT++;
                        Assert.True(row[6].AsBoolean(), "auto-refreshed index should be is_valid = true");
                    }
                }
            }
            finally { batch.Dispose(); }
        }
        Assert.True(rowsForT > 0, "auto-refreshed table 't' should surface entries in datum_catalog.indexes");
    }

    // ──────────────────── helpers ────────────────────

    private async Task<string> IngestAndIndex(string fileName)
    {
        const string csv =
            "id,name\n" +
            "1,alice\n" +
            "2,bob\n" +
            "3,carol\n" +
            "4,dave\n";

        string datumPath = Path.Combine(_tempDir, fileName);
        MemoryFileDescriptor source = new(csv, fileName: "test.csv");
        OutputDescriptor destination = new(datumPath);

        FormatRegistry registry = new([new CsvFileFormat()]);
        Pool pool = new(new PoolBacking());
        Ingester ingester = new(registry, pool);
        await ingester.IngestAsync(source, destination);

        DatumFileDescriptor datumSource = new(datumPath);
        OutputDescriptor indexDest = new(Path.ChangeExtension(datumPath, ".datum-index"));
        Indexer indexer = new(pool);
        await indexer.IndexAsync(datumSource, indexDest);

        return datumPath;
    }

    private static async IAsyncEnumerable<RowBatch> MakeBatchesMatchingSchema(
        Pool pool, Schema schema, object[][] rows)
    {
        string[] columnNames = schema.Columns.Select(c => c.Name).ToArray();
        ColumnLookup lookup = new(columnNames);
        Arena arena = new();
        RowBatch batch = pool.RentRowBatch(lookup, capacity: rows.Length, arena: arena);
        foreach (object[] row in rows)
        {
            DataValue[] values = pool.RentDataValues(columnNames.Length);
            for (int c = 0; c < columnNames.Length; c++)
            {
                DataKind kind = schema.Columns[c].Kind;
                values[c] = (row[c], kind) switch
                {
                    (int i, DataKind.UInt8) => DataValue.FromUInt8((byte)i),
                    (int i, DataKind.UInt16) => DataValue.FromUInt16((ushort)i),
                    (int i, DataKind.UInt32) => DataValue.FromUInt32((uint)i),
                    (int i, DataKind.Int32) => DataValue.FromInt32(i),
                    (int i, DataKind.Int64) => DataValue.FromInt64(i),
                    (string s, DataKind.String) => DataValue.FromString(s, arena),
                    _ => throw new NotSupportedException(
                        $"test cell {row[c].GetType().Name} -> {kind} not yet supported."),
                };
            }
            batch.Add(values);
        }
        yield return batch;
        await Task.CompletedTask;
    }
}
