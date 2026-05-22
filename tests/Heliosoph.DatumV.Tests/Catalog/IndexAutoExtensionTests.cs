using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Providers;
using Heliosoph.DatumV.Indexing;
using Heliosoph.DatumV.Indexing.Bitmap;
using Heliosoph.DatumV.Indexing.Bloom;
using Heliosoph.DatumV.Ingestion;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;
using Heliosoph.DatumV.Serialization;
using Heliosoph.DatumV.Serialization.Csv;

namespace Heliosoph.DatumV.Tests.Catalog;

/// <summary>
/// PR13a-2 tests for the two-phase commit that auto-refreshes
/// <c>.datum-index</c> after every INSERT. The unlock: users no
/// longer need to run a manual <c>REINDEX</c> after data mutations
/// to restore acceleration. The failure-mode contract: if the index
/// refresh fails for any reason, the data commit stands and the
/// index surfaces as <see cref="IndexValidity.Stale"/> via
/// <c>system.indexes.is_valid = false</c>.
/// </summary>
public sealed class IndexAutoExtensionTests : ServiceTestBase, IAsyncLifetime
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

        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        ITableProvider provider = catalog.Add(new TableDescriptor("t", datumPath));

        Assert.NotNull(provider.GetSourceIndex());
        Assert.Equal(IndexValidity.Valid, provider.GetIndexValidity());

        Schema schema = provider.GetSchema();
        await catalog["t"].AppendRowsAsync(
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
        Pool pool = CreatePool();
        string catalogPath = Path.Combine(_tempDir, ".datum-catalog.json");
        using TableCatalog catalog = CreateCatalog(catalogPath);

        catalog.Plan("CREATE TABLE t (id Int32, name String)");
        ITableProvider provider = catalog["t"];

        Assert.Equal(IndexValidity.Missing, provider.GetIndexValidity());

        catalog.Plan("INSERT INTO t VALUES (1, 'a'), (2, 'b')");

        Assert.Equal(IndexValidity.Valid, provider.GetIndexValidity());
        Assert.NotNull(provider.GetSourceIndex());

        string indexPath = Path.ChangeExtension(Path.Combine(_tempDir, "data", "public", "t.datum"), ".datum-index");
        Assert.True(File.Exists(indexPath), "first INSERT should have produced the .datum-index");
    }

    [Fact]
    public async Task IsValid_ColumnSurfaces_StaleSentinelRow()
    {
        // After a forced manual invalidation, the index appears in
        // system.indexes with is_valid = false so users can
        // spot the table that needs REINDEX.
        string datumPath = await IngestAndIndex("stale_sentinel.datum");

        using TableCatalog catalog = CreateCatalog();
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
        using TableCatalog reopened = CreateCatalog();
        ITableProvider reopenedProvider = reopened.Add(new TableDescriptor("t", datumPath));

        Assert.Equal(IndexValidity.Stale, reopenedProvider.GetIndexValidity());
        Assert.Null(reopenedProvider.GetSourceIndex());

        // Query system.indexes — table 't' should appear with
        // is_valid = false.
        bool sawStaleRow = false;
        await foreach (RowBatch batch in reopened["system.indexes"]
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
        Assert.True(sawStaleRow, "stale table 't' should surface in system.indexes with is_valid = false");
    }

    [Fact]
    public async Task IsValid_ColumnSurfaces_TrueAfterAutoRefresh()
    {
        // After an INSERT, the index is auto-refreshed; the column
        // entries appear with is_valid = true.
        string datumPath = await IngestAndIndex("valid_after_append.datum");

        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        ITableProvider provider = catalog.Add(new TableDescriptor("t", datumPath));

        Schema schema = provider.GetSchema();
        await catalog["t"].AppendRowsAsync(
            MakeBatchesMatchingSchema(pool, schema, [[7, "rev"]]),
            CancellationToken.None);

        // Find the row(s) in system.indexes for table 't'. Every
        // entry should have is_valid = true.
        int rowsForT = 0;
        await foreach (RowBatch batch in catalog["system.indexes"]
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
        Assert.True(rowsForT > 0, "auto-refreshed table 't' should surface entries in system.indexes");
    }

    [Fact]
    public async Task Append_ChunkCountGrows_AfterInsert()
    {
        // PR13b chunk-splice contract: an INSERT appends one or more
        // new chunks past the prefix's last chunk. The total chunk
        // count after auto-refresh must be (existing + delta), not
        // (delta) — the latter would indicate a full rebuild that
        // somehow lost track of pre-INSERT chunks.
        string datumPath = await IngestAndIndex("chunk_growth.datum");

        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        ITableProvider provider = catalog.Add(new TableDescriptor("t", datumPath));

        SourceIndex? before = provider.GetSourceIndex();
        Assert.NotNull(before);
        int chunkCountBefore = before.Chunks.Count;
        long rowsBefore = before.Schema.TotalRowCount;

        Schema schema = provider.GetSchema();
        await catalog["t"].AppendRowsAsync(
            MakeBatchesMatchingSchema(pool, schema, [[10, "ten"], [11, "eleven"]]),
            CancellationToken.None);

        SourceIndex? after = provider.GetSourceIndex();
        Assert.NotNull(after);
        Assert.True(after.Chunks.Count > chunkCountBefore,
            $"chunk count should grow after append (before={chunkCountBefore}, after={after.Chunks.Count})");
        Assert.Equal(rowsBefore + 2, after.Schema.TotalRowCount);
    }

    [Fact]
    public async Task Append_BitmapPreservesExistingValues()
    {
        // PR13b carry-forward correctness: a bitmap entry for a value
        // that existed before the INSERT must still pin the correct
        // pre-INSERT chunk(s) post-INSERT. This exercises the merge
        // path that copies existing chunks' compressed bitmaps
        // verbatim into the new sidecar.
        string datumPath = await IngestAndIndex("bitmap_preserve.datum");

        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        ITableProvider provider = catalog.Add(new TableDescriptor("t", datumPath));

        SourceIndex? before = provider.GetSourceIndex();
        Assert.NotNull(before);

        // Find a column with a bitmap index built from the original 4
        // rows (id 1..4 / name alice/bob/carol/dave).
        Assert.NotNull(before.BitmapIndexes);
        IReadOnlySet<int> chunksForBobBefore = FindChunksContainingString(before, "name", "bob");
        Assert.NotEmpty(chunksForBobBefore);

        Schema schema = provider.GetSchema();
        await catalog["t"].AppendRowsAsync(
            MakeBatchesMatchingSchema(pool, schema, [[5, "extra"]]),
            CancellationToken.None);

        SourceIndex? after = provider.GetSourceIndex();
        Assert.NotNull(after);
        Assert.NotNull(after.BitmapIndexes);

        IReadOnlySet<int> chunksForBobAfter = FindChunksContainingString(after, "name", "bob");
        // The chunk index numbering is stable across the merge — old
        // chunk 0 stays chunk 0 — so 'bob' must still pin the same set.
        Assert.Equal(chunksForBobBefore, chunksForBobAfter);
    }

    [Fact]
    public async Task Append_BitmapFindsValuesAddedByInsert()
    {
        // PR13b bitmap INSERT correctness: a value that exists ONLY in
        // newly-appended rows must be findable via the bitmap, in a
        // chunk index past the prefix's last chunk.
        string datumPath = await IngestAndIndex("bitmap_new_value.datum");

        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        ITableProvider provider = catalog.Add(new TableDescriptor("t", datumPath));

        SourceIndex? before = provider.GetSourceIndex();
        Assert.NotNull(before);
        int chunkCountBefore = before.Chunks.Count;

        Schema schema = provider.GetSchema();
        await catalog["t"].AppendRowsAsync(
            MakeBatchesMatchingSchema(pool, schema, [[99, "zara"]]),
            CancellationToken.None);

        SourceIndex? after = provider.GetSourceIndex();
        Assert.NotNull(after);
        Assert.NotNull(after.BitmapIndexes);

        IReadOnlySet<int> chunksForZara = FindChunksContainingString(after, "name", "zara");
        Assert.NotEmpty(chunksForZara);
        // The new value can only appear in a chunk past the original
        // chunk count — proves the splice attached the delta past the
        // prefix instead of accidentally remapping chunk indices.
        Assert.All(chunksForZara, c => Assert.True(c >= chunksForBefore(before),
            $"chunk {c} for new value 'zara' must be >= existing chunk count {chunkCountBefore}"));

        // Old values still resolve (carry-forward correctness).
        IReadOnlySet<int> chunksForAlice = FindChunksContainingString(after, "name", "alice");
        Assert.NotEmpty(chunksForAlice);

        static int chunksForBefore(SourceIndex sx) => sx.Chunks.Count;
    }

    [Fact]
    public async Task Update_AutoRefreshesIndex_WithoutManualReindex()
    {
        // PR13c: UPDATE no longer leaves .datum-index Stale until the
        // user runs REINDEX. The full rebuild fires inside the UPDATE
        // mutation lock so the next GetSourceIndex call returns a
        // valid snapshot whose values reflect the rewrite.
        string datumPath = await IngestAndIndex("update_auto_refresh.datum");

        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        ITableProvider provider = catalog.Add(new TableDescriptor("t", datumPath));

        Assert.NotNull(provider.GetSourceIndex());
        Assert.Equal(IndexValidity.Valid, provider.GetIndexValidity());

        catalog.Plan("UPDATE t SET name = 'renamed' WHERE id = 2");

        // Without PR13c this would be null (Stale) and require REINDEX.
        Assert.NotNull(provider.GetSourceIndex());
        Assert.Equal(IndexValidity.Valid, provider.GetIndexValidity());
    }

    [Fact]
    public async Task Update_BitmapFindsNewValue_NoManualReindex()
    {
        // PR13c correctness: UPDATE replaces a value; the bitmap on
        // the post-UPDATE index must locate the new value (which
        // didn't exist pre-UPDATE) in the chunk that holds the
        // rewritten row. Without auto-refresh the new value would be
        // a false negative — bitmap/bloom would say "absent in all
        // chunks" → indexed lookup misses the row.
        string datumPath = await IngestAndIndex("update_bitmap.datum");

        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        ITableProvider provider = catalog.Add(new TableDescriptor("t", datumPath));

        SourceIndex? before = provider.GetSourceIndex();
        Assert.NotNull(before);
        Assert.NotNull(before.BitmapIndexes);

        // Pre-UPDATE: 'renamed' is not present anywhere.
        IReadOnlySet<int> chunksForRenamedBefore = FindChunksContainingString(before, "name", "renamed");
        Assert.Empty(chunksForRenamedBefore);

        catalog.Plan("UPDATE t SET name = 'renamed' WHERE id = 2");

        SourceIndex? after = provider.GetSourceIndex();
        Assert.NotNull(after);
        Assert.NotNull(after.BitmapIndexes);

        IReadOnlySet<int> chunksForRenamedAfter = FindChunksContainingString(after, "name", "renamed");
        Assert.NotEmpty(chunksForRenamedAfter);
    }

    [Fact]
    public async Task Update_BloomMembership_RecognizesNewValue()
    {
        // PR13c bloom correctness: bloom for the affected chunk must
        // test-positive for the new value post-UPDATE. Without
        // auto-refresh, bloom would say "definitely absent" — a
        // false negative that prunes the chunk from indexed scans.
        string datumPath = await IngestAndIndex("update_bloom.datum");

        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        ITableProvider provider = catalog.Add(new TableDescriptor("t", datumPath));

        catalog.Plan("UPDATE t SET name = 'morphed' WHERE id = 3");

        SourceIndex? after = provider.GetSourceIndex();
        Assert.NotNull(after);
        Assert.NotNull(after.BloomFilters);

        // All 4 original rows live in chunk 0; UPDATE didn't add
        // chunks. Bloom for chunk 0 must test-positive for 'morphed'.
        Assert.True(after.BloomFilters.TryGetFilter("name", 0, out BloomFilter? bloomChunk0));
        Arena bloomArena = CreateArena();
        Assert.True(bloomChunk0.MayContain(DataValue.FromString("morphed"), bloomArena));
    }

    [Fact]
    public async Task Append_BloomMembershipPreserved()
    {
        // PR13b bloom carry-forward: a value present in the prefix's
        // bloom filter must still test-positive after an unrelated
        // INSERT. Bloom is per-chunk, so we check the chunk that
        // covered the original row.
        string datumPath = await IngestAndIndex("bloom_preserve.datum");

        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        ITableProvider provider = catalog.Add(new TableDescriptor("t", datumPath));

        SourceIndex? before = provider.GetSourceIndex();
        Assert.NotNull(before);
        Assert.NotNull(before.BloomFilters);

        int chunkCountBefore = before.Chunks.Count;

        Schema schema = provider.GetSchema();
        await catalog["t"].AppendRowsAsync(
            MakeBatchesMatchingSchema(pool, schema, [[42, "newcomer"]]),
            CancellationToken.None);

        SourceIndex? after = provider.GetSourceIndex();
        Assert.NotNull(after);
        Assert.NotNull(after.BloomFilters);

        // Bloom for old chunk 0 must still test-positive for an old
        // value present in that chunk. Existing 4 rows all live in
        // chunk 0 (chunkSize >> 4).
        Assert.True(after.BloomFilters.TryGetFilter("name", 0, out BloomFilter? bloomChunk0));
        Arena bloomArena = CreateArena();
        Assert.True(bloomChunk0.MayContain(DataValue.FromString("alice"), bloomArena));

        // Bloom array grew with the delta chunks.
        Assert.True(after.BloomFilters.ChunkCount > chunkCountBefore);
    }

    private static IReadOnlySet<int> FindChunksContainingString(
        SourceIndex sx, string column, string value)
    {
        Assert.NotNull(sx.BitmapIndexes);
        Assert.True(sx.BitmapIndexes.TryGetIndex(column, out BitmapColumnIndex? col));
        return col.FindChunksContaining(DataValue.FromString(value));
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
