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
/// PR9.5 regression: <see cref="DatumFileTableProviderV2"/> must reject
/// a <c>.datum-index</c> sidecar whose stored fingerprint no longer
/// matches the current <c>.datum</c> file. Without this check, every
/// PR8/PR9 mutation (AddColumn / DropColumn / AppendRows / DeleteRows)
/// silently invalidates the index but the provider keeps using it —
/// new rows go missing from indexed queries, dropped columns produce
/// undefined behaviour.
/// </summary>
public sealed class StaleIndexDetectionTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_pr95_{Guid.NewGuid():N}");

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
    public async Task FreshIndex_GetSourceIndex_Returns_NonNull()
    {
        // Sanity: the baseline behaviour must keep working — an unmodified
        // .datum + .datum-index pair surfaces the index normally.
        string datumPath = await IngestAndIndex("baseline.datum");

        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        ITableProvider provider = catalog.Add(new TableDescriptor("t", datumPath));

        Assert.NotNull(provider.GetSourceIndex());
    }

    [Fact]
    public async Task ReopenAfterAppend_IndexAutoRefreshed()
    {
        // Cross-process simulation: ingest + index, then mutate (in a
        // first catalog), then reopen the file in a second catalog.
        // PR13a-2 wired auto-rebuild into AppendRows' commit path, so
        // the second catalog now sees a fresh index — the PR9.5
        // staleness rejection still protects against torn writes and
        // mutations that bypass the commit path (AddColumn / DropColumn /
        // DeleteRows still invalidate without auto-rebuild), exercised
        // in the sibling tests below.
        string datumPath = await IngestAndIndex("after_append.datum");
        string indexPath = Path.ChangeExtension(datumPath, ".datum-index");
        Assert.True(File.Exists(indexPath), "index sidecar should exist after IndexAsync");

        Pool pool = CreatePool();

        // First open: append a row, dispose. The append's commit path
        // auto-refreshes .datum-index (PR13a-2 two-phase commit).
        using (TableCatalog firstCatalog = CreateCatalog(pool))
        {
            ITableProvider firstProvider = firstCatalog.Add(new TableDescriptor("t", datumPath));
            Schema schema = firstProvider.GetSchema();
            await firstCatalog["t"].AppendRowsAsync(
                MakeBatchesMatchingSchema(pool, schema, [[5, "extra"]]),
                CancellationToken.None);
        }

        Assert.True(File.Exists(indexPath));

        // Second open: provider sees the freshly-rebuilt index, not a
        // torn / stale one.
        using TableCatalog secondCatalog = CreateCatalog(pool);
        ITableProvider provider = secondCatalog.Add(new TableDescriptor("t", datumPath));

        Assert.NotNull(provider.GetSourceIndex());
        Assert.Equal(IndexValidity.Valid, provider.GetIndexValidity());
    }

    [Fact]
    public async Task SameProvider_AfterAppend_GetSourceIndex_AutoRefreshed()
    {
        // In-process simulation: open the provider (which caches the
        // index), mutate it, query GetSourceIndex from the SAME provider.
        // PR13a-2 auto-rebuilds .datum-index inside the AppendRows
        // commit path, so the post-mutation provider sees a freshly
        // rebuilt index instead of a null (PR9.5's invalidate-on-mutate
        // contract for AppendRows is now "rebuild on mutate").
        string datumPath = await IngestAndIndex("same_provider.datum");

        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        ITableProvider provider = catalog.Add(new TableDescriptor("t", datumPath));

        Assert.NotNull(provider.GetSourceIndex());

        Schema schema = provider.GetSchema();
        await catalog["t"].AppendRowsAsync(
            MakeBatchesMatchingSchema(pool, schema, [[6, "ghost"]]),
            CancellationToken.None);

        Assert.NotNull(provider.GetSourceIndex());
        Assert.Equal(IndexValidity.Valid, provider.GetIndexValidity());
    }

    [Fact]
    public async Task SameProvider_AfterAddColumn_GetSourceIndex_ReturnsNull()
    {
        // AddColumn changes the file's footer (new column block) → file
        // size + stripe hash differ → fingerprint mismatch.
        string datumPath = await IngestAndIndex("after_add_col.datum");

        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        ITableProvider provider = catalog.Add(new TableDescriptor("t", datumPath));
        Assert.NotNull(provider.GetSourceIndex());

        catalog["t"].AddColumn(new ColumnInfo("note", DataKind.String, nullable: true));

        Assert.Null(provider.GetSourceIndex());
    }

    [Fact]
    public async Task SameProvider_AfterDeleteRows_GetSourceIndex_ReturnsNull()
    {
        string datumPath = await IngestAndIndex("after_delete.datum");

        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        ITableProvider provider = catalog.Add(new TableDescriptor("t", datumPath));
        Assert.NotNull(provider.GetSourceIndex());

        catalog["t"].DeleteRows([0L]);

        Assert.Null(provider.GetSourceIndex());
    }

    // ──────────────────── Helpers ────────────────────

    private async Task<string> IngestAndIndex(string fileName)
    {
        // CSV with two columns. Inline only — no sidecar in play.
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
        Pool pool = CreatePool();
        Ingester ingester = new(registry, pool);
        await ingester.IngestAsync(source, destination);

        DatumFileDescriptor datumSource = new(datumPath);
        OutputDescriptor indexDest = new(Path.ChangeExtension(datumPath, ".datum-index"));
        Indexer indexer = new(pool);
        await indexer.IndexAsync(datumSource, indexDest);

        return datumPath;
    }

    /// <summary>
    /// Builds an <see cref="IAsyncEnumerable{RowBatch}"/> whose batches
    /// match <paramref name="schema"/>'s column kinds — necessary
    /// because the CSV ingester narrows numeric kinds to the smallest
    /// fitting type (e.g. small ints become UInt8) and the provider's
    /// append path validates kind on write.
    /// </summary>
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
                    (int i, DataKind.UInt64) => DataValue.FromUInt64((ulong)i),
                    (int i, DataKind.Int8) => DataValue.FromInt8((sbyte)i),
                    (int i, DataKind.Int16) => DataValue.FromInt16((short)i),
                    (int i, DataKind.Int32) => DataValue.FromInt32(i),
                    (int i, DataKind.Int64) => DataValue.FromInt64(i),
                    (string s, DataKind.String) => DataValue.FromString(s, arena),
                    _ => throw new NotSupportedException(
                        $"test cell {row[c].GetType().Name} -> {kind} not yet supported in MakeBatchesMatchingSchema."),
                };
            }
            batch.Add(values);
        }
        yield return batch;
        await Task.CompletedTask;
    }
}
