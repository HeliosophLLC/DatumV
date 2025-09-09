using DatumIngest.Catalog;
using DatumIngest.Indexing;
using DatumIngest.Model;

namespace DatumIngest.Tests.Indexing;

public sealed class SourceIndexBuilderTests
{
    [Fact]
    public async Task BuildAsync_EmptyProvider_ProducesEmptyIndex()
    {
        InMemoryTableProvider provider = new([]);
        TableDescriptor descriptor = CreateDescriptor("empty");
        SourceIndexBuilder builder = new(chunkSize: 5);

        SourceIndex index = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        Assert.Equal(0, index.Schema.TotalRowCount);
        Assert.Empty(index.Chunks);
    }

    [Fact]
    public async Task BuildAsync_SingleChunk_CapturesSchemaAndStats()
    {
        Row[] rows =
        [
            MakeRow(("id", DataValue.FromFloat32(1.0f)), ("name", DataValue.FromString("alice"))),
            MakeRow(("id", DataValue.FromFloat32(2.0f)), ("name", DataValue.FromString("bob"))),
            MakeRow(("id", DataValue.FromFloat32(3.0f)), ("name", DataValue.FromString("charlie"))),
        ];

        InMemoryTableProvider provider = new(rows);
        TableDescriptor descriptor = CreateDescriptor("test");
        SourceIndexBuilder builder = new(chunkSize: 100);

        SourceIndex index = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        Assert.Equal(3, index.Schema.TotalRowCount);
        Assert.Equal(2, index.Schema.Schema.Columns.Count);
        Assert.Single(index.Chunks);
        Assert.Equal(0, index.Chunks[0].RowOffset);
        Assert.Equal(3, index.Chunks[0].RowCount);
    }

    [Fact]
    public async Task BuildAsync_MultipleChunks_SplitsCorrectly()
    {
        Row[] rows = Enumerable.Range(0, 25).Select(i =>
            MakeRow(("value", DataValue.FromFloat32((float)i)))).ToArray();

        InMemoryTableProvider provider = new(rows);
        TableDescriptor descriptor = CreateDescriptor("data");
        SourceIndexBuilder builder = new(chunkSize: 10);

        SourceIndex index = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        Assert.Equal(25, index.Schema.TotalRowCount);
        Assert.Equal(3, index.Chunks.Count);

        Assert.Equal(0, index.Chunks[0].RowOffset);
        Assert.Equal(10, index.Chunks[0].RowCount);

        Assert.Equal(10, index.Chunks[1].RowOffset);
        Assert.Equal(10, index.Chunks[1].RowCount);

        Assert.Equal(20, index.Chunks[2].RowOffset);
        Assert.Equal(5, index.Chunks[2].RowCount);
    }

    [Fact]
    public async Task BuildAsync_TracksMinMax_ForScalarColumn()
    {
        Row[] rows =
        [
            MakeRow(("x", DataValue.FromFloat32(10.0f))),
            MakeRow(("x", DataValue.FromFloat32(5.0f))),
            MakeRow(("x", DataValue.FromFloat32(20.0f))),
            MakeRow(("x", DataValue.FromFloat32(1.0f))),
        ];

        InMemoryTableProvider provider = new(rows);
        TableDescriptor descriptor = CreateDescriptor("minmax");
        SourceIndexBuilder builder = new(chunkSize: 100);

        SourceIndex index = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        ChunkColumnStatistics stats = index.Chunks[0].ColumnStatistics["x"];
        Assert.Equal(1.0f, stats.Minimum.GetValueOrDefault().AsFloat32());
        Assert.Equal(20.0f, stats.Maximum.GetValueOrDefault().AsFloat32());
    }

    [Fact]
    public async Task BuildAsync_TracksNullCount()
    {
        Row[] rows =
        [
            MakeRow(("x", DataValue.FromFloat32(1.0f))),
            MakeRow(("x", DataValue.Null(DataKind.Float32))),
            MakeRow(("x", DataValue.Null(DataKind.Float32))),
            MakeRow(("x", DataValue.FromFloat32(2.0f))),
        ];

        InMemoryTableProvider provider = new(rows);
        TableDescriptor descriptor = CreateDescriptor("nulls");
        SourceIndexBuilder builder = new(chunkSize: 100);

        SourceIndex index = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        ChunkColumnStatistics stats = index.Chunks[0].ColumnStatistics["x"];
        Assert.Equal(2, stats.NullCount);
    }

    [Fact]
    public async Task BuildAsync_WithStream_ComputesFingerprint()
    {
        Row[] rows = [MakeRow(("x", DataValue.FromFloat32(1.0f)))];
        byte[] fileContent = new byte[256];
        Random.Shared.NextBytes(fileContent);

        InMemoryTableProvider provider = new(rows);
        TableDescriptor descriptor = CreateDescriptor("fingerprinted");
        SourceIndexBuilder builder = new(chunkSize: 100);

        using MemoryStream stream = new(fileContent);
        SourceIndex index = await builder.BuildAsync(descriptor, provider, stream, CancellationToken.None);

        Assert.Equal(256, index.Fingerprint.FileSize);
        Assert.Equal(32, index.Fingerprint.StripedHash.Length);
    }

    [Fact]
    public async Task BuildAsync_WithoutStream_ProducesEmptyFingerprint()
    {
        Row[] rows = [MakeRow(("x", DataValue.FromFloat32(1.0f)))];
        InMemoryTableProvider provider = new(rows);
        TableDescriptor descriptor = CreateDescriptor("no-stream");
        SourceIndexBuilder builder = new(chunkSize: 100);

        SourceIndex index = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        Assert.Equal(0, index.Fingerprint.FileSize);
        Assert.Empty(index.Fingerprint.StripedHash);
    }

    [Fact]
    public async Task BuildAsync_EstimatesCardinality()
    {
        Row[] rows = Enumerable.Range(0, 100).Select(i =>
            MakeRow(("category", DataValue.FromString(i < 50 ? "A" : "B")))).ToArray();

        InMemoryTableProvider provider = new(rows);
        TableDescriptor descriptor = CreateDescriptor("cardinality");
        SourceIndexBuilder builder = new(chunkSize: 200);

        SourceIndex index = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        ChunkColumnStatistics stats = index.Chunks[0].ColumnStatistics["category"];
        // HyperLogLog estimate; with only 2 distinct values it should be close to 2.
        Assert.InRange(stats.EstimatedCardinality, 1, 5);
    }

    [Fact]
    public void IncrementalBuilder_ProducesSameResultAsFullBuild()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        SourceIndexBuilder builder = new(chunkSize: 5);
        IncrementalIndexBuilder incremental = builder.CreateIncrementalBuilder(fingerprint);

        Row[] rows = Enumerable.Range(0, 12).Select(i =>
            MakeRow(("value", DataValue.FromFloat32((float)i)))).ToArray();

        foreach (Row row in rows)
        {
            incremental.AddRow(row);
        }

        SourceIndex index = incremental.Finalize();

        Assert.Equal(12, index.Schema.TotalRowCount);
        Assert.Equal(3, index.Chunks.Count);

        Assert.Equal(0, index.Chunks[0].RowOffset);
        Assert.Equal(5, index.Chunks[0].RowCount);

        Assert.Equal(5, index.Chunks[1].RowOffset);
        Assert.Equal(5, index.Chunks[1].RowCount);

        Assert.Equal(10, index.Chunks[2].RowOffset);
        Assert.Equal(2, index.Chunks[2].RowCount);
    }

    [Fact]
    public void IncrementalBuilder_NoRows_ProducesEmptyIndex()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        SourceIndexBuilder builder = new(chunkSize: 10);
        IncrementalIndexBuilder incremental = builder.CreateIncrementalBuilder(fingerprint);

        SourceIndex index = incremental.Finalize();

        Assert.Equal(0, index.Schema.TotalRowCount);
        Assert.Empty(index.Chunks);
    }

    [Fact]
    public async Task BuildAsync_StringMinMax_TracksCorrectly()
    {
        Row[] rows =
        [
            MakeRow(("name", DataValue.FromString("charlie"))),
            MakeRow(("name", DataValue.FromString("alice"))),
            MakeRow(("name", DataValue.FromString("bob"))),
        ];

        InMemoryTableProvider provider = new(rows);
        TableDescriptor descriptor = CreateDescriptor("strings");
        SourceIndexBuilder builder = new(chunkSize: 100);

        SourceIndex index = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        ChunkColumnStatistics stats = index.Chunks[0].ColumnStatistics["name"];
        Assert.Equal("alice", stats.Minimum.GetValueOrDefault().AsString());
        Assert.Equal("charlie", stats.Maximum.GetValueOrDefault().AsString());
    }

    [Fact]
    public async Task BuildAsync_WithBloomColumns_ProducesBloomFilters()
    {
        Row[] rows = Enumerable.Range(0, 20).Select(i =>
            MakeRow(("id", DataValue.FromFloat32((float)i)),
                    ("name", DataValue.FromString($"name_{i}")))).ToArray();

        InMemoryTableProvider provider = new(rows);
        TableDescriptor descriptor = CreateDescriptor("bloom");
        HashSet<string> bloomColumns = new(StringComparer.OrdinalIgnoreCase) { "id" };
        SourceIndexBuilder builder = new(chunkSize: 10, bloomColumns: bloomColumns);

        SourceIndex index = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        Assert.NotNull(index.BloomFilters);
        Assert.True(index.BloomFilters.HasColumn("id"));
        Assert.False(index.BloomFilters.HasColumn("name"));
        Assert.Equal(2, index.BloomFilters.ChunkCount);

        // Values in chunk 0 (ids 0-9) should be found.
        Assert.True(index.BloomFilters.TryGetFilter("id", 0, out BloomFilter? chunk0));
        Assert.True(chunk0!.MayContain(DataValue.FromFloat32(5.0f)));

        // Values in chunk 1 (ids 10-19) should be found.
        Assert.True(index.BloomFilters.TryGetFilter("id", 1, out BloomFilter? chunk1));
        Assert.True(chunk1!.MayContain(DataValue.FromFloat32(15.0f)));

        // Value 15 should NOT be in chunk 0 (it was in chunk 1).
        Assert.False(chunk0.MayContain(DataValue.FromFloat32(15.0f)));
    }

    [Fact]
    public async Task BuildAsync_WithoutBloomColumns_NoBloomFilters()
    {
        Row[] rows = [MakeRow(("id", DataValue.FromFloat32(1.0f)))];
        InMemoryTableProvider provider = new(rows);
        TableDescriptor descriptor = CreateDescriptor("no-bloom");
        SourceIndexBuilder builder = new(chunkSize: 100);

        SourceIndex index = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        Assert.Null(index.BloomFilters);
    }

    [Fact]
    public void IncrementalBuilder_WithBloomColumns_ProducesBloomFilters()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        HashSet<string> bloomColumns = new(StringComparer.OrdinalIgnoreCase) { "category" };
        SourceIndexBuilder builder = new(chunkSize: 5, bloomColumns: bloomColumns);
        IncrementalIndexBuilder incremental = builder.CreateIncrementalBuilder(fingerprint);

        for (int index = 0; index < 10; index++)
        {
            incremental.AddRow(MakeRow(
                ("category", DataValue.FromString(index < 5 ? "A" : "B"))));
        }

        SourceIndex result = incremental.Finalize();

        Assert.NotNull(result.BloomFilters);
        Assert.True(result.BloomFilters.HasColumn("category"));
        Assert.Equal(2, result.BloomFilters.ChunkCount);

        Assert.True(result.BloomFilters.TryGetFilter("category", 0, out BloomFilter? filter0));
        Assert.True(filter0!.MayContain(DataValue.FromString("A")));
        Assert.False(filter0.MayContain(DataValue.FromString("B")));

        Assert.True(result.BloomFilters.TryGetFilter("category", 1, out BloomFilter? filter1));
        Assert.True(filter1!.MayContain(DataValue.FromString("B")));
        Assert.False(filter1.MayContain(DataValue.FromString("A")));
    }

    [Fact]
    public async Task BuildAsync_WithIndexColumns_BuildsSortedIndexes()
    {
        Row[] rows =
        [
            MakeRow(("id", DataValue.FromFloat32(3.0f)), ("name", DataValue.FromString("charlie"))),
            MakeRow(("id", DataValue.FromFloat32(1.0f)), ("name", DataValue.FromString("alice"))),
            MakeRow(("id", DataValue.FromFloat32(2.0f)), ("name", DataValue.FromString("bob"))),
        ];

        InMemoryTableProvider provider = new(rows);
        TableDescriptor descriptor = CreateDescriptor("test");
        HashSet<string> indexColumns = ["id"];
        SourceIndexBuilder builder = new(chunkSize: 100, indexColumns: indexColumns);

        SourceIndex index = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        Assert.NotNull(index.SortedIndexes);
        Assert.True(index.SortedIndexes.HasColumn("id"));
        Assert.False(index.SortedIndexes.HasColumn("name"));

        Assert.True(index.SortedIndexes.TryGetIndex("id", out SortedValueIndex? sortedIndex));
        Assert.Equal(3, sortedIndex!.Count);

        IReadOnlyList<ValueIndexEntry> found = sortedIndex.FindExact(DataValue.FromFloat32(2.0f));
        Assert.Single(found);
        Assert.Equal(0, found[0].ChunkIndex);
        Assert.Equal(2, found[0].RowOffsetInChunk);
    }

    [Fact]
    public async Task BuildAsync_WithIndexColumns_MultipleChunks_CorrectChunkIndexes()
    {
        Row[] rows = Enumerable.Range(0, 15).Select(i =>
            MakeRow(("value", DataValue.FromFloat32((float)i)))).ToArray();

        InMemoryTableProvider provider = new(rows);
        TableDescriptor descriptor = CreateDescriptor("multi");
        HashSet<string> indexColumns = ["value"];
        SourceIndexBuilder builder = new(chunkSize: 5, indexColumns: indexColumns);

        SourceIndex index = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        Assert.NotNull(index.SortedIndexes);
        Assert.True(index.SortedIndexes.TryGetIndex("value", out SortedValueIndex? sortedIndex));
        Assert.Equal(15, sortedIndex!.Count);

        // Value 0.0 should be in chunk 0, row 0.
        IReadOnlyList<ValueIndexEntry> found0 = sortedIndex.FindExact(DataValue.FromFloat32(0.0f));
        Assert.Single(found0);
        Assert.Equal(0, found0[0].ChunkIndex);
        Assert.Equal(0, found0[0].RowOffsetInChunk);

        // Value 7.0 should be in chunk 1, row 2 (rows 5-9 are chunk 1).
        IReadOnlyList<ValueIndexEntry> found7 = sortedIndex.FindExact(DataValue.FromFloat32(7.0f));
        Assert.Single(found7);
        Assert.Equal(1, found7[0].ChunkIndex);
        Assert.Equal(2, found7[0].RowOffsetInChunk);

        // Value 12.0 should be in chunk 2, row 2 (rows 10-14 are chunk 2).
        IReadOnlyList<ValueIndexEntry> found12 = sortedIndex.FindExact(DataValue.FromFloat32(12.0f));
        Assert.Single(found12);
        Assert.Equal(2, found12[0].ChunkIndex);
        Assert.Equal(2, found12[0].RowOffsetInChunk);
    }

    [Fact]
    public async Task BuildAsync_WithoutIndexColumns_NoSortedIndexes()
    {
        Row[] rows = [MakeRow(("id", DataValue.FromFloat32(1.0f)))];
        InMemoryTableProvider provider = new(rows);
        TableDescriptor descriptor = CreateDescriptor("test");
        SourceIndexBuilder builder = new(chunkSize: 100);

        SourceIndex index = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        Assert.Null(index.SortedIndexes);
    }

    [Fact]
    public async Task BuildAsync_WithIndexColumns_SkipsNullValues()
    {
        Row[] rows =
        [
            MakeRow(("id", DataValue.FromFloat32(1.0f))),
            MakeRow(("id", DataValue.Null(DataKind.Float32))),
            MakeRow(("id", DataValue.FromFloat32(3.0f))),
        ];

        InMemoryTableProvider provider = new(rows);
        TableDescriptor descriptor = CreateDescriptor("nulls");
        HashSet<string> indexColumns = ["id"];
        SourceIndexBuilder builder = new(chunkSize: 100, indexColumns: indexColumns);

        SourceIndex index = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        Assert.NotNull(index.SortedIndexes);
        Assert.True(index.SortedIndexes.TryGetIndex("id", out SortedValueIndex? sortedIndex));
        // Null values should be excluded from the sorted index.
        Assert.Equal(2, sortedIndex!.Count);
    }

    [Fact]
    public void IncrementalBuilder_WithIndexColumns_BuildsSortedIndexes()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        HashSet<string> indexColumns = ["id"];
        SourceIndexBuilder builder = new(chunkSize: 100, indexColumns: indexColumns);
        IncrementalIndexBuilder incremental = builder.CreateIncrementalBuilder(fingerprint);

        incremental.AddRow(MakeRow(("id", DataValue.FromFloat32(3.0f))));
        incremental.AddRow(MakeRow(("id", DataValue.FromFloat32(1.0f))));
        incremental.AddRow(MakeRow(("id", DataValue.FromFloat32(2.0f))));

        SourceIndex result = incremental.Finalize();

        // Finalize no longer materializes sorted indexes (they remain on disk in the spill writer).
        Assert.Null(result.SortedIndexes);

        // Verify sorted index data via the spill writer.
        SortedValueIndexSet? sortedIndexes = incremental.SpillWriter?.BuildSortedValueIndexSet();
        Assert.NotNull(sortedIndexes);
        Assert.True(sortedIndexes.HasColumn("id"));
        Assert.True(sortedIndexes.TryGetIndex("id", out SortedValueIndex? sortedIndex));
        Assert.Equal(3, sortedIndex!.Count);

        IReadOnlyList<ValueIndexEntry> found = sortedIndex.FindExact(DataValue.FromFloat32(2.0f));
        Assert.Single(found);
        incremental.Dispose();
    }

    [Fact]
    public async Task BuildAsync_WithBothBloomAndIndexColumns_BuildsBoth()
    {
        Row[] rows =
        [
            MakeRow(("id", DataValue.FromFloat32(1.0f)), ("category", DataValue.FromString("A"))),
            MakeRow(("id", DataValue.FromFloat32(2.0f)), ("category", DataValue.FromString("B"))),
        ];

        InMemoryTableProvider provider = new(rows);
        TableDescriptor descriptor = CreateDescriptor("both");
        HashSet<string> bloomColumns = ["category"];
        HashSet<string> indexColumns = ["id"];
        SourceIndexBuilder builder = new(chunkSize: 100, bloomColumns: bloomColumns, indexColumns: indexColumns);

        SourceIndex index = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        Assert.NotNull(index.BloomFilters);
        Assert.NotNull(index.SortedIndexes);
        Assert.True(index.BloomFilters.HasColumn("category"));
        Assert.True(index.SortedIndexes.HasColumn("id"));
    }

    // ───────────── BuildSetAsync ─────────────

    [Fact]
    public async Task BuildSetAsync_SingleTable_ProducesCorrectIndexSet()
    {
        Row[] rows =
        [
            MakeRow(("id", DataValue.FromFloat32(1.0f))),
            MakeRow(("id", DataValue.FromFloat32(2.0f))),
        ];

        InMemoryTableProvider provider = new(rows);
        TableDescriptor descriptor = CreateDescriptor("orders");
        SourceIndexBuilder builder = new(chunkSize: 100);

        SourceIndexSet indexSet = await builder.BuildSetAsync(
            [(descriptor, provider)], sourceStream: null, CancellationToken.None);

        Assert.Single(indexSet.Tables);
        Assert.True(indexSet.Tables.ContainsKey("orders"));
        Assert.Equal(2, indexSet.Tables["orders"].Schema.TotalRowCount);
    }

    [Fact]
    public async Task BuildSetAsync_MultipleTables_ProducesAllEntries()
    {
        Row[] ordersRows =
        [
            MakeRow(("id", DataValue.FromFloat32(1.0f)), ("total", DataValue.FromFloat32(99.0f))),
            MakeRow(("id", DataValue.FromFloat32(2.0f)), ("total", DataValue.FromFloat32(42.0f))),
        ];

        Row[] itemsRows =
        [
            MakeRow(("orderId", DataValue.FromFloat32(1.0f)), ("product", DataValue.FromString("widget"))),
        ];

        InMemoryTableProvider ordersProvider = new(ordersRows);
        InMemoryTableProvider itemsProvider = new(itemsRows);
        TableDescriptor ordersDescriptor = CreateDescriptor("orders");
        TableDescriptor itemsDescriptor = CreateDescriptor("orders.items");
        SourceIndexBuilder builder = new(chunkSize: 100);

        SourceIndexSet indexSet = await builder.BuildSetAsync(
            [(ordersDescriptor, ordersProvider), (itemsDescriptor, itemsProvider)],
            sourceStream: null, CancellationToken.None);

        Assert.Equal(2, indexSet.Tables.Count);
        Assert.True(indexSet.Tables.ContainsKey("orders"));
        Assert.True(indexSet.Tables.ContainsKey("orders.items"));
        Assert.Equal(2, indexSet.Tables["orders"].Schema.TotalRowCount);
        Assert.Equal(1, indexSet.Tables["orders.items"].Schema.TotalRowCount);
    }

    [Fact]
    public async Task BuildSetAsync_SharesFingerprint_AcrossAllTables()
    {
        Row[] rows = [MakeRow(("x", DataValue.FromFloat32(1.0f)))];
        InMemoryTableProvider provider1 = new(rows);
        InMemoryTableProvider provider2 = new(rows);
        SourceIndexBuilder builder = new(chunkSize: 100);

        SourceFingerprint fingerprint = new(123, new byte[] { 1, 2, 3 });
        SourceIndexSet indexSet = await builder.BuildSetAsync(
            [(CreateDescriptor("a"), provider1), (CreateDescriptor("b"), provider2)],
            sourceStream: null, fingerprint, CancellationToken.None);

        Assert.Equal(fingerprint, indexSet.Fingerprint);
        Assert.Equal(fingerprint, indexSet.Tables["a"].Fingerprint);
        Assert.Equal(fingerprint, indexSet.Tables["b"].Fingerprint);
    }

    [Fact]
    public async Task BuildSetAsync_RoundTrips_ThroughWriterReader()
    {
        Row[] rows =
        [
            MakeRow(("value", DataValue.FromFloat32(1.0f))),
            MakeRow(("value", DataValue.FromFloat32(2.0f))),
        ];

        InMemoryTableProvider provider1 = new(rows);
        InMemoryTableProvider provider2 = new(rows);
        SourceIndexBuilder builder = new(chunkSize: 100);

        SourceIndexSet original = await builder.BuildSetAsync(
            [(CreateDescriptor("alpha"), provider1), (CreateDescriptor("beta"), provider2)],
            sourceStream: null, CancellationToken.None);

        string tempFile = Path.GetTempFileName();
        try
        {
            using (FileStream stream = File.Create(tempFile))
            {
                UnifiedIndexWriter.Write(original, stream);
            }
            using MappedSourceIndexSet mapped = UnifiedIndexReader.Open(tempFile);
            SourceIndexSet restored = mapped.IndexSet;

            Assert.Equal(original.Tables.Count, restored.Tables.Count);
            Assert.True(restored.Tables.ContainsKey("alpha"));
            Assert.True(restored.Tables.ContainsKey("beta"));
            Assert.Equal(2, restored.Tables["alpha"].Schema.TotalRowCount);
            Assert.Equal(2, restored.Tables["beta"].Schema.TotalRowCount);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task BuildAsync_WithBloomAllColumns_ProducesBloomFiltersForEveryColumn()
    {
        Row[] rows = Enumerable.Range(0, 20).Select(i =>
            MakeRow(("id", DataValue.FromFloat32((float)i)),
                    ("name", DataValue.FromString($"name_{i}")),
                    ("category", DataValue.FromString(i % 2 == 0 ? "even" : "odd")))).ToArray();

        InMemoryTableProvider provider = new(rows);
        TableDescriptor descriptor = CreateDescriptor("bloom-all");
        SourceIndexBuilder builder = new(bloomAllColumns: true, indexAllColumns: false, chunkSize: 10);

        SourceIndex index = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        Assert.NotNull(index.BloomFilters);
        Assert.True(index.BloomFilters.HasColumn("id"));
        Assert.True(index.BloomFilters.HasColumn("name"));
        Assert.True(index.BloomFilters.HasColumn("category"));
        Assert.Equal(2, index.BloomFilters.ChunkCount);
    }

    [Fact]
    public async Task BuildAsync_WithIndexAllColumns_BuildsSortedIndexesForEveryColumn()
    {
        Row[] rows =
        [
            MakeRow(("id", DataValue.FromFloat32(3.0f)), ("name", DataValue.FromString("charlie"))),
            MakeRow(("id", DataValue.FromFloat32(1.0f)), ("name", DataValue.FromString("alice"))),
            MakeRow(("id", DataValue.FromFloat32(2.0f)), ("name", DataValue.FromString("bob"))),
        ];

        InMemoryTableProvider provider = new(rows);
        TableDescriptor descriptor = CreateDescriptor("index-all");
        SourceIndexBuilder builder = new(bloomAllColumns: false, indexAllColumns: true, chunkSize: 100);

        SourceIndex index = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        Assert.NotNull(index.SortedIndexes);
        Assert.True(index.SortedIndexes.HasColumn("id"));
        Assert.True(index.SortedIndexes.HasColumn("name"));
    }

    [Fact]
    public void IncrementalBuilder_WithBloomAllColumns_ProducesBloomFiltersForEveryColumn()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        SourceIndexBuilder builder = new(bloomAllColumns: true, indexAllColumns: false, chunkSize: 5);
        IncrementalIndexBuilder incremental = builder.CreateIncrementalBuilder(fingerprint);

        for (int index = 0; index < 10; index++)
        {
            incremental.AddRow(MakeRow(
                ("id", DataValue.FromFloat32((float)index)),
                ("category", DataValue.FromString(index < 5 ? "A" : "B"))));
        }

        SourceIndex result = incremental.Finalize();

        Assert.NotNull(result.BloomFilters);
        Assert.True(result.BloomFilters.HasColumn("id"));
        Assert.True(result.BloomFilters.HasColumn("category"));
        Assert.Equal(2, result.BloomFilters.ChunkCount);
    }

    // ───────────── Auto-Index Heuristic ─────────────

    [Fact]
    public async Task BuildAsync_AutoIndex_IndexesCompactColumnsOnly()
    {
        // Use >256 rows so high-cardinality columns exceed bitmap threshold and get sorted.
        // Boolean has inherently ≤2 distinct values → always gets bitmap under auto-cascade.
        int count = 300;
        Row[] rows = Enumerable.Range(0, count).Select(i =>
            MakeRow(
                ("id", DataValue.FromFloat32((float)i)),
                ("name", DataValue.FromString($"user_{i}")),
                ("data", DataValue.FromVector(new float[] { (float)i })),
                ("active", DataValue.FromBoolean(i % 2 == 0)),
                ("created", DataValue.FromDate(new DateOnly(2024, 1, 1).AddDays(i))))).ToArray();

        InMemoryTableProvider provider = new(rows);
        TableDescriptor descriptor = CreateDescriptor("auto");
        SourceIndexBuilder builder = new(
            bloomAllColumns: false, indexAllColumns: false, chunkSize: count, autoIndexColumns: true);

        SourceIndex index = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        Assert.NotNull(index.SortedIndexes);
        // High-cardinality compact types should get sorted indexes.
        Assert.True(index.SortedIndexes.HasColumn("id"));
        Assert.True(index.SortedIndexes.HasColumn("name"));
        Assert.True(index.SortedIndexes.HasColumn("created"));
        // Boolean (≤2 distinct) gets bitmap, not sorted.
        Assert.False(index.SortedIndexes.HasColumn("active"));
        Assert.NotNull(index.BitmapIndexes);
        Assert.True(index.BitmapIndexes.TryGetIndex("active", out _));
        // Vectors should NOT be indexed.
        Assert.False(index.SortedIndexes.HasColumn("data"));
    }

    [Fact]
    public async Task BuildAsync_AutoIndex_DropsLongStringColumns()
    {
        // >256 distinct values so id and short_text get sorted indexes (not bitmap).
        int count = 300;
        Row[] rows = Enumerable.Range(0, count).Select(i =>
            MakeRow(
                ("id", DataValue.FromFloat32((float)i)),
                ("short_text", DataValue.FromString($"s{i}")),
                ("long_text", DataValue.FromString($"this is way too long for auto-indexing {i}")))).ToArray();

        InMemoryTableProvider provider = new(rows);
        TableDescriptor descriptor = CreateDescriptor("auto-drop");
        SourceIndexBuilder builder = new(
            bloomAllColumns: false, indexAllColumns: false, chunkSize: count, autoIndexColumns: true);

        SourceIndex index = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        Assert.NotNull(index.SortedIndexes);
        Assert.True(index.SortedIndexes.HasColumn("id"));
        Assert.True(index.SortedIndexes.HasColumn("short_text"));
        // Long text column should be dropped.
        Assert.False(index.SortedIndexes.HasColumn("long_text"));
    }

    [Fact]
    public async Task BuildAsync_AutoIndex_SpillsAcrossMultipleChunks()
    {
        // >256 distinct values for id so it gets a sorted index (not bitmap).
        int count = 300;
        Row[] rows = Enumerable.Range(0, count).Select(i =>
            MakeRow(
                ("id", DataValue.FromFloat32((float)i)),
                ("tag", DataValue.FromString($"t{i % 5}")))).ToArray();

        InMemoryTableProvider provider = new(rows);
        TableDescriptor descriptor = CreateDescriptor("spill");
        SourceIndexBuilder builder = new(
            bloomAllColumns: false, indexAllColumns: false, chunkSize: 50, autoIndexColumns: true);

        SourceIndex index = await builder.BuildAsync(descriptor, provider, null, CancellationToken.None);

        Assert.NotNull(index.SortedIndexes);
        Assert.True(index.SortedIndexes.TryGetIndex("id", out SortedValueIndex? idIndex));
        Assert.Equal(count, idIndex!.Count);

        // Verify correct chunk assignment after spill+merge.
        IReadOnlyList<ValueIndexEntry> found = idIndex.FindExact(DataValue.FromFloat32(112.0f));
        Assert.Single(found);
        Assert.Equal(2, found[0].ChunkIndex); // rows 100-149 are chunk 2
        Assert.Equal(12, found[0].RowOffsetInChunk);
    }

    [Fact]
    public void IncrementalBuilder_AutoIndex_IndexesCompactColumns()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        SourceIndexBuilder builder = new(
            bloomAllColumns: false, indexAllColumns: false, chunkSize: 500, autoIndexColumns: true);
        IncrementalIndexBuilder incremental = builder.CreateIncrementalBuilder(fingerprint);

        // >256 distinct values so id stays in spill writer (not deduped by bitmap).
        for (int i = 0; i < 300; i++)
        {
            incremental.AddRow(MakeRow(
                ("id", DataValue.FromFloat32((float)i)),
                ("vec", DataValue.FromVector(new float[] { (float)i }))));
        }

        SourceIndex result = incremental.Finalize();
        Assert.Null(result.SortedIndexes);

        SortedValueIndexSet? sortedIndexes = incremental.SpillWriter?.BuildSortedValueIndexSet();
        Assert.NotNull(sortedIndexes);
        Assert.True(sortedIndexes.HasColumn("id"));
        Assert.False(sortedIndexes.HasColumn("vec"));
        incremental.Dispose();
    }

    [Fact]
    public void IncrementalBuilder_AutoIndex_DropsLongStrings()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        SourceIndexBuilder builder = new(
            bloomAllColumns: false, indexAllColumns: false, chunkSize: 500, autoIndexColumns: true);
        IncrementalIndexBuilder incremental = builder.CreateIncrementalBuilder(fingerprint);

        // >256 distinct short codes so code stays in spill writer.
        for (int i = 0; i < 300; i++)
        {
            incremental.AddRow(MakeRow(
                ("code", DataValue.FromString($"C{i}")),
                ("description", DataValue.FromString($"A very long description that exceeds the limit {i}"))));
        }

        SourceIndex result = incremental.Finalize();
        Assert.Null(result.SortedIndexes);

        SortedValueIndexSet? sortedIndexes = incremental.SpillWriter?.BuildSortedValueIndexSet();
        Assert.NotNull(sortedIndexes);
        Assert.True(sortedIndexes.HasColumn("code"));
        Assert.False(sortedIndexes.HasColumn("description"));
        incremental.Dispose();
    }

    [Fact]
    public void IncrementalBuilder_AutoIndex_SpillsAndMergesCorrectly()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        SourceIndexBuilder builder = new(
            bloomAllColumns: false, indexAllColumns: false, chunkSize: 50, autoIndexColumns: true);
        IncrementalIndexBuilder incremental = builder.CreateIncrementalBuilder(fingerprint);

        // >256 distinct values so bitmap doesn't capture them.
        int count = 300;
        for (int i = 0; i < count; i++)
        {
            incremental.AddRow(MakeRow(("value", DataValue.FromFloat32((float)i))));
        }

        SourceIndex result = incremental.Finalize();
        Assert.Null(result.SortedIndexes);

        SortedValueIndexSet? sortedIndexes = incremental.SpillWriter?.BuildSortedValueIndexSet();
        Assert.NotNull(sortedIndexes);
        Assert.True(sortedIndexes.TryGetIndex("value", out SortedValueIndex? sortedIndex));
        Assert.Equal(count, sortedIndex!.Count);

        // Value 57.0 should be in chunk 1, row 7.
        IReadOnlyList<ValueIndexEntry> found = sortedIndex.FindExact(DataValue.FromFloat32(57.0f));
        Assert.Single(found);
        Assert.Equal(1, found[0].ChunkIndex);
        Assert.Equal(7, found[0].RowOffsetInChunk);
        incremental.Dispose();
    }

    [Theory]
    [InlineData(DataKind.Float32, true)]
    [InlineData(DataKind.Float64, true)]
    [InlineData(DataKind.UInt8, true)]
    [InlineData(DataKind.Int8, true)]
    [InlineData(DataKind.Int16, true)]
    [InlineData(DataKind.UInt16, true)]
    [InlineData(DataKind.Int32, true)]
    [InlineData(DataKind.UInt32, true)]
    [InlineData(DataKind.Int64, true)]
    [InlineData(DataKind.UInt64, true)]
    [InlineData(DataKind.Boolean, true)]
    [InlineData(DataKind.Date, true)]
    [InlineData(DataKind.DateTime, true)]
    [InlineData(DataKind.Time, true)]
    [InlineData(DataKind.Duration, true)]
    [InlineData(DataKind.Uuid, true)]
    [InlineData(DataKind.String, true)]
    [InlineData(DataKind.Vector, false)]
    [InlineData(DataKind.Matrix, false)]
    [InlineData(DataKind.Tensor, false)]
    [InlineData(DataKind.UInt8Array, false)]
    [InlineData(DataKind.Image, false)]
    [InlineData(DataKind.JsonValue, false)]
    [InlineData(DataKind.Array, false)]
    public void IsAutoIndexableKind_CategorizesCorrectly(DataKind kind, bool expected)
    {
        Assert.Equal(expected, SourceIndexBuilder.IsAutoIndexableKind(kind));
    }

    // ───────────── Helpers ─────────────

    private static TableDescriptor CreateDescriptor(string name)
    {
        Dictionary<string, string> options = new()
        {
            [TableCatalog.SubTableKeyOption] = name,
        };
        return new TableDescriptor("test", name, $"{name}.test", options);
    }

    private static Row MakeRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = columns.Select(c => c.Name).ToArray();
        DataValue[] values = columns.Select(c => c.Value).ToArray();
        return new Row(names, values);
    }

    /// <summary>
    /// Simple in-memory provider for testing. Yields pre-built rows.
    /// </summary>
    private sealed class InMemoryTableProvider : ITableProvider
    {
        private readonly Row[] _rows;

        public InMemoryTableProvider(Row[] rows)
        {
            _rows = rows;
        }

        public Task<Schema> GetSchemaAsync(TableDescriptor descriptor, CancellationToken cancellationToken)
        {
            if (_rows.Length == 0)
            {
                return Task.FromResult(new Schema([new ColumnInfo("empty", DataKind.String, nullable: true)]));
            }

            List<ColumnInfo> columns = new();

            foreach (string name in _rows[0].ColumnNames)
            {
                columns.Add(new ColumnInfo(name, _rows[0][name].Kind, nullable: true));
            }

            return Task.FromResult(new Schema(columns));
        }

        public Task<ProviderCapabilities> GetCapabilitiesAsync(
            TableDescriptor descriptor, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ProviderCapabilities(
                EstimatedRowCount: _rows.Length,
                EstimatedRowSizeBytes: null,
                SupportsSeek: false,
                ColumnCosts: new Dictionary<string, ColumnCost>()));
        }

        public async IAsyncEnumerable<RowBatch> OpenAsync(
            TableDescriptor descriptor,
            IReadOnlySet<string>? requiredColumns,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            RowBatch batch = RowBatch.Rent(64);

            foreach (Row row in _rows)
            {
                batch.Add(row);

                if (batch.IsFull)
                {
                    yield return batch;
                    batch = RowBatch.Rent(64);
                }
            }

            if (batch.Count > 0)
            {
                yield return batch;
            }

            await Task.CompletedTask;
        }
    }
}
