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
public sealed class StaleIndexDetectionTests : IAsyncLifetime
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

        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        ITableProvider provider = catalog.Add(new TableDescriptor("t", datumPath));

        Assert.NotNull(provider.GetSourceIndex());
    }

    [Fact]
    public async Task ReopenAfterAppend_StaleIndex_IsRejected()
    {
        // Cross-process simulation: ingest + index, then mutate (in a
        // first catalog), then reopen the file in a second catalog.
        // The second catalog's provider must detect the stale index
        // by fingerprint mismatch and surface GetSourceIndex() as null.
        string datumPath = await IngestAndIndex("after_append.datum");
        string indexPath = Path.ChangeExtension(datumPath, ".datum-index");
        Assert.True(File.Exists(indexPath), "index sidecar should exist after IndexAsync");

        Pool pool = new(new PoolBacking());

        // First open: append a row, dispose.
        using (TableCatalog firstCatalog = new(pool))
        {
            ITableProvider firstProvider = firstCatalog.Add(new TableDescriptor("t", datumPath));
            Schema schema = firstProvider.GetSchema();
            await firstCatalog.AppendRowsAsync("t",
                MakeBatchesMatchingSchema(pool, schema, [[5, "extra"]]),
                CancellationToken.None);
        }

        // The stale .datum-index file is still on disk.
        Assert.True(File.Exists(indexPath),
            "PR9.5 chooses passive invalidation — the stale sidecar stays on disk.");

        // Second open: provider must detect the stale index and refuse it.
        using TableCatalog secondCatalog = new(pool);
        ITableProvider provider = secondCatalog.Add(new TableDescriptor("t", datumPath));

        Assert.Null(provider.GetSourceIndex());
    }

    [Fact]
    public async Task SameProvider_AfterAppend_GetSourceIndex_ReturnsNull()
    {
        // In-process simulation: open the provider (which caches the
        // index), mutate it, query GetSourceIndex from the SAME provider.
        // The cached index must be invalidated on mutation — otherwise
        // queries through the post-mutation provider would still use the
        // stale index.
        string datumPath = await IngestAndIndex("same_provider.datum");

        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        ITableProvider provider = catalog.Add(new TableDescriptor("t", datumPath));

        Assert.NotNull(provider.GetSourceIndex());

        Schema schema = provider.GetSchema();
        await catalog.AppendRowsAsync("t",
            MakeBatchesMatchingSchema(pool, schema, [[6, "ghost"]]),
            CancellationToken.None);

        Assert.Null(provider.GetSourceIndex());
    }

    [Fact]
    public async Task SameProvider_AfterAddColumn_GetSourceIndex_ReturnsNull()
    {
        // AddColumn changes the file's footer (new column block) → file
        // size + stripe hash differ → fingerprint mismatch.
        string datumPath = await IngestAndIndex("after_add_col.datum");

        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        ITableProvider provider = catalog.Add(new TableDescriptor("t", datumPath));
        Assert.NotNull(provider.GetSourceIndex());

        catalog.AddColumn("t", new ColumnInfo("note", DataKind.String, nullable: true));

        Assert.Null(provider.GetSourceIndex());
    }

    [Fact]
    public async Task SameProvider_AfterDeleteRows_GetSourceIndex_ReturnsNull()
    {
        string datumPath = await IngestAndIndex("after_delete.datum");

        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        ITableProvider provider = catalog.Add(new TableDescriptor("t", datumPath));
        Assert.NotNull(provider.GetSourceIndex());

        catalog.DeleteRows("t", [0L]);

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
        Pool pool = new(new PoolBacking());
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
