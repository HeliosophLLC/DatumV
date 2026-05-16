using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Indexing;
using DatumIngest.Ingestion;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Pooling;
using DatumIngest.Serialization;
using DatumIngest.Serialization.Csv;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// PR12 — <c>REINDEX</c> rebuilds a table's <c>.datum-index</c> sidecar
/// from current data. Replaces the passive-invalidation behaviour that
/// PR9.5 introduced: after a mutation the cached index is dropped and
/// <c>GetSourceIndex</c> returns null until the user runs REINDEX.
/// </summary>
public sealed class ReindexExecutorTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_pr12_{Guid.NewGuid():N}");

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
    public async Task Reindex_AfterAppend_StaysValid()
    {
        // PR13a-2 makes AppendRows auto-rebuild .datum-index in its
        // commit path, so the post-append index is already Valid;
        // REINDEX is now a no-op (rebuilds an already-current file).
        // The semantic this test pinned previously — "REINDEX recovers
        // a stale index after a mutation" — is exercised by the
        // AfterDelete / AfterUpdate variants below, since DELETE and
        // UPDATE still go through the invalidate-on-mutate path.
        string datumPath = await IngestAndIndex("after_append.datum");

        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        ITableProvider provider = catalog.Add(new TableDescriptor("t", datumPath));

        Assert.NotNull(provider.GetSourceIndex());

        Schema schema = provider.GetSchema();
        await catalog["t"].AppendRowsAsync(
            MakeBatchesMatchingSchema(pool, schema, [[5, "extra"]]),
            CancellationToken.None);

        // PR13a-2: post-append index is already Valid (auto-rebuilt).
        Assert.NotNull(provider.GetSourceIndex());

        // REINDEX still works on an already-current file.
        catalog.Plan("REINDEX t");
        Assert.NotNull(provider.GetSourceIndex());
        Assert.True(File.Exists(Path.ChangeExtension(datumPath, ".datum-index")));
    }

    [Fact]
    public async Task Reindex_FreshFile_StaysValid()
    {
        // REINDEX on an already-current file must not corrupt anything —
        // it overwrites the sidecar and re-loads it; the result is still
        // valid and queries continue to use it.
        string datumPath = await IngestAndIndex("fresh.datum");

        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        ITableProvider provider = catalog.Add(new TableDescriptor("t", datumPath));

        SourceIndex? before = provider.GetSourceIndex();
        Assert.NotNull(before);

        catalog.Plan("REINDEX t");

        SourceIndex? after = provider.GetSourceIndex();
        Assert.NotNull(after);
    }

    [Fact]
    public async Task Reindex_AfterDelete_RestoresSourceIndex()
    {
        string datumPath = await IngestAndIndex("after_delete.datum");

        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        ITableProvider provider = catalog.Add(new TableDescriptor("t", datumPath));

        Assert.NotNull(provider.GetSourceIndex());

        catalog["t"].DeleteRows([0L]);
        Assert.Null(provider.GetSourceIndex());

        catalog.Plan("REINDEX t");
        Assert.NotNull(provider.GetSourceIndex());
    }

    [Fact]
    public void Reindex_MissingTable_Throws()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);

        ExecutionException ex = Assert.Throws<ExecutionException>(
            () => catalog.Plan("REINDEX missing"));
        Assert.Contains("not registered", ex.Message);
    }

    [Fact]
    public void Reindex_TempTable_Rejected()
    {
        // In-memory tables have no .datum-index — REINDEX must surface a
        // clear error rather than silently no-op.
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32, name String)");

        ExecutionException ex = Assert.Throws<ExecutionException>(
            () => catalog.Plan("REINDEX t"));
        Assert.Contains("does not support REINDEX", ex.Message);
    }

    [Fact]
    public async Task Analyze_AfterAppend_StaysValid()
    {
        // ANALYZE is an alias for REINDEX in this build. Post-PR13a-2,
        // AppendRows auto-rebuilds the index in its commit path, so
        // the index is already Valid before ANALYZE runs — same shape
        // as Reindex_AfterAppend_StaysValid.
        string datumPath = await IngestAndIndex("analyze_after_append.datum");

        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        ITableProvider provider = catalog.Add(new TableDescriptor("t", datumPath));
        Assert.NotNull(provider.GetSourceIndex());

        Schema schema = provider.GetSchema();
        await catalog["t"].AppendRowsAsync(
            MakeBatchesMatchingSchema(pool, schema, [[5, "extra"]]),
            CancellationToken.None);
        Assert.NotNull(provider.GetSourceIndex());

        catalog.Plan("ANALYZE t");

        Assert.NotNull(provider.GetSourceIndex());
    }

    [Fact]
    public void Analyze_TempTable_Rejected()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32, name String)");

        ExecutionException ex = Assert.Throws<ExecutionException>(
            () => catalog.Plan("ANALYZE t"));
        Assert.Contains("does not support ANALYZE", ex.Message);
    }

    // ──────────────────── PR14i — manifest refresh ────────────────────

    [Fact]
    public async Task Analyze_OnFileWithoutManifest_CreatesOne()
    {
        // The CSV-ingest path used by IngestAndIndex doesn't write a
        // .datum-manifest sidecar. ANALYZE should create one on demand by
        // scanning the rows.
        string datumPath = await IngestAndIndex("analyze_creates_manifest.datum");
        string manifestPath = Path.ChangeExtension(datumPath, ".datum-manifest");
        Assert.False(File.Exists(manifestPath));

        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        ITableProvider provider = catalog.Add(new TableDescriptor("t", datumPath));

        catalog.Plan("ANALYZE t");

        Assert.True(File.Exists(manifestPath));
        QueryResultsManifest? after = provider.GetManifest();
        Assert.NotNull(after);
        Assert.NotEmpty(after!.Features);
    }

    [Fact]
    public async Task Analyze_FreshlyIngestedFile_PopulatesNumericMean()
    {
        string datumPath = await IngestAndIndex("analyze_mean.datum");

        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        ITableProvider provider = catalog.Add(new TableDescriptor("t", datumPath));

        catalog.Plan("ANALYZE t");

        QueryResultsManifest? after = provider.GetManifest();
        Assert.NotNull(after);

        // The "id" column has values {1, 2, 3, 4} → mean = 2.5.
        FeatureManifest idFeature = after!.Features.First(f => f.Name == "id");
        NumericFeatureManifest numeric = Assert.IsType<NumericFeatureManifest>(idFeature);
        Assert.Equal(2.5, numeric.Mean, 1e-10);
    }

    [Fact]
    public async Task Analyze_StatsValidFlag_IsTrueAfterRefresh()
    {
        string datumPath = await IngestAndIndex("analyze_valid_flag.datum");

        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        ITableProvider provider = catalog.Add(new TableDescriptor("t", datumPath));

        catalog.Plan("ANALYZE t");

        QueryResultsManifest? after = provider.GetManifest();
        Assert.NotNull(after);
        Assert.All(after!.Features, f => Assert.True(f.CachedStatsValid));
    }

    // ──────────────────── PR14j — mutation staleness ────────────────────

    [Fact]
    public async Task Mutation_FlipsCachedStatsValidToFalse()
    {
        // After a mutation, every column's CachedStatsValid should report
        // false until ANALYZE re-runs. This is the consumer-facing signal
        // that planner / language-server should discount the cached
        // expensive fields (top-K, quantiles, histogram, entropy).
        string datumPath = await IngestAndIndex("staleness_after_insert.datum");

        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        ITableProvider provider = catalog.Add(new TableDescriptor("t", datumPath));

        catalog.Plan("ANALYZE t");
        QueryResultsManifest? before = provider.GetManifest();
        Assert.NotNull(before);
        Assert.All(before!.Features, f => Assert.True(f.CachedStatsValid));

        Schema schema = provider.GetSchema();
        await catalog["t"].AppendRowsAsync(
            MakeBatchesMatchingSchema(pool, schema, [[5, "extra"]]),
            CancellationToken.None);

        QueryResultsManifest? after = provider.GetManifest();
        Assert.NotNull(after);
        Assert.All(after!.Features, f => Assert.False(f.CachedStatsValid));
    }

    [Fact]
    public async Task Analyze_AfterMutation_RestoresCachedStatsValid()
    {
        string datumPath = await IngestAndIndex("staleness_restored_by_analyze.datum");

        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        ITableProvider provider = catalog.Add(new TableDescriptor("t", datumPath));

        catalog.Plan("ANALYZE t");

        Schema schema = provider.GetSchema();
        await catalog["t"].AppendRowsAsync(
            MakeBatchesMatchingSchema(pool, schema, [[5, "extra"]]),
            CancellationToken.None);

        // After mutation: stale.
        QueryResultsManifest? mid = provider.GetManifest();
        Assert.All(mid!.Features, f => Assert.False(f.CachedStatsValid));

        // After ANALYZE: fresh again.
        catalog.Plan("ANALYZE t");
        QueryResultsManifest? after = provider.GetManifest();
        Assert.All(after!.Features, f => Assert.True(f.CachedStatsValid));
    }

    [Fact]
    public async Task Update_FlipsCachedStatsValidToFalse()
    {
        string datumPath = await IngestAndIndex("staleness_after_update.datum");

        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        ITableProvider provider = catalog.Add(new TableDescriptor("t", datumPath));

        catalog.Plan("ANALYZE t");

        catalog.Plan("UPDATE t SET name = 'updated' WHERE id = 1");

        QueryResultsManifest? after = provider.GetManifest();
        Assert.NotNull(after);
        Assert.All(after!.Features, f => Assert.False(f.CachedStatsValid));
    }

    [Fact]
    public async Task Reindex_AfterUpdate_StaysValid()
    {
        // PR13c: UPDATE auto-refreshes .datum-index in its commit
        // path (full rebuild, not extend — UPDATE rewrites existing
        // chunks rather than appending). The post-update index is
        // already Valid; REINDEX is now a no-op (rebuilds an
        // already-current file).
        string datumPath = await IngestAndIndex("after_update.datum");

        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        ITableProvider provider = catalog.Add(new TableDescriptor("t", datumPath));
        Assert.NotNull(provider.GetSourceIndex());

        catalog.Plan("UPDATE t SET name = 'updated' WHERE id = 1");
        Assert.NotNull(provider.GetSourceIndex());
        Assert.Equal(IndexValidity.Valid, provider.GetIndexValidity());

        // REINDEX still works on an already-current file.
        catalog.Plan("REINDEX t");
        Assert.NotNull(provider.GetSourceIndex());
    }

    // ──────────────────── Helpers ────────────────────

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
        Pool pool = CreatePool();
        Ingester ingester = new(registry, pool);
        await ingester.IngestAsync(source, destination);

        DatumFileDescriptor datumSource = new(datumPath);
        OutputDescriptor indexDest = new(Path.ChangeExtension(datumPath, ".datum-index"));
        Indexer indexer = new(pool);
        await indexer.IndexAsync(datumSource, indexDest);

        return datumPath;
    }

    private async IAsyncEnumerable<RowBatch> MakeBatchesMatchingSchema(
        Pool pool, Schema schema, object[][] rows)
    {
        string[] columnNames = schema.Columns.Select(c => c.Name).ToArray();
        ColumnLookup lookup = new(columnNames);
        Arena arena = CreateArena();
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
                        $"test cell {row[c].GetType().Name} -> {kind} not yet supported."),
                };
            }
            batch.Add(values);
        }
        yield return batch;
        await Task.CompletedTask;
    }
}
