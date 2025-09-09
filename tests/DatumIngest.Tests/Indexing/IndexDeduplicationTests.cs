using DatumIngest.Catalog;
using DatumIngest.Indexing;
using DatumIngest.Indexing.Bitmap;
using DatumIngest.Indexing.BTree;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// Verifies that the index builder does not produce duplicate indexes for the same column
/// across different index types. A low-cardinality column should appear in <em>either</em>
/// the bitmap index set <em>or</em> the sorted/B+Tree index set, never both.
/// </summary>
public sealed class IndexDeduplicationTests
{
    /// <summary>
    /// Reproduces the duplication bug: a low-cardinality auto-indexable column gets both
    /// a bitmap index (cardinality ≤ 256) and a sorted/B+Tree index entry from the spill
    /// writer. After the fix, the bitmap index should win and the column should be excluded
    /// from the sorted index set.
    /// </summary>
    [Fact]
    public async Task BuildAsync_LowCardinalityColumn_NotInBothBitmapAndSortedIndexes()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        string[] categories = ["electronics", "clothing", "food"];

        // Auto-index enabled, enough rows to exercise the pipeline.
        SourceIndexBuilder builder = new(
            bloomAllColumns: false,
            indexAllColumns: false,
            chunkSize: 100,
            autoIndexColumns: true);

        TestTableProvider provider = new(
            ["category"],
            Enumerable.Range(0, 300).Select(i =>
                new DataValue[] { DataValue.FromString(categories[i % 3]) }));

        SourceIndex index = await builder.BuildAsync(
            TestTableDescriptor.Default, provider, sourceStream: null, fingerprint, CancellationToken.None);

        // The column should have a bitmap index (3 distinct values ≤ 256).
        Assert.NotNull(index.BitmapIndexes);
        Assert.True(index.BitmapIndexes.TryGetIndex("category", out _));

        // The column must NOT also appear in the sorted index set.
        if (index.SortedIndexes is not null)
        {
            Assert.False(
                index.SortedIndexes.HasColumn("category"),
                "Low-cardinality column 'category' should not appear in both BitmapIndexes and SortedIndexes.");
        }
    }

    /// <summary>
    /// Same deduplication check using the <see cref="IncrementalIndexBuilder"/> path
    /// (fused pipeline / output co-generation).
    /// </summary>
    [Fact]
    public void IncrementalBuilder_LowCardinalityColumn_NotInBothBitmapAndSortedIndexes()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        string[] categories = ["electronics", "clothing", "food"];

        SourceIndexBuilder builder = new(
            bloomAllColumns: false,
            indexAllColumns: false,
            chunkSize: 100,
            autoIndexColumns: true);

        IncrementalIndexBuilder incremental = builder.CreateIncrementalBuilder(fingerprint);

        for (int i = 0; i < 300; i++)
        {
            string[] names = ["category"];
            DataValue[] values = [DataValue.FromString(categories[i % 3])];
            incremental.AddRow(new Row(names, values));
        }

        SourceIndex index = incremental.Finalize();

        Assert.NotNull(index.BitmapIndexes);
        Assert.True(index.BitmapIndexes.TryGetIndex("category", out _));

        // The spill writer may still have entries, but after serialization round-trip
        // through IndexWriter (which drops bitmap columns from the spill writer), the
        // column must not appear in both index types.
        // Verify via round-trip serialization.
        SourceIndexSet indexSet = new(fingerprint, new Dictionary<string, SourceIndex> { ["test"] = index });

        string tempFile1 = Path.GetTempFileName();
        try
        {
            using (FileStream stream = File.Create(tempFile1))
            {
                UnifiedIndexWriter.Write(indexSet, stream, incremental.SpillWriter);
            }
            using MappedSourceIndexSet mapped = UnifiedIndexReader.Open(tempFile1);
            SourceIndex roundTripped = mapped.IndexSet.Tables["test"];

            Assert.NotNull(roundTripped.BitmapIndexes);
            Assert.True(roundTripped.BitmapIndexes.TryGetIndex("category", out _));

            if (roundTripped.SortedIndexes is not null)
            {
                Assert.False(
                    roundTripped.SortedIndexes.HasColumn("category"),
                    "After round-trip, low-cardinality column 'category' should not appear in sorted indexes.");
            }

            if (roundTripped.BPlusTreeIndexes is not null)
            {
                Assert.False(
                    roundTripped.BPlusTreeIndexes.TryGetIndex("category", out _),
                    "After round-trip, low-cardinality column 'category' should not appear in B+Tree indexes.");
            }

            incremental.Dispose();
        }
        finally
        {
            File.Delete(tempFile1);
        }
    }

    /// <summary>
    /// A high-cardinality column should still get a sorted index and no bitmap index.
    /// </summary>
    [Fact]
    public async Task BuildAsync_HighCardinalityColumn_OnlyInSortedIndex()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        int count = IndexConstants.BitmapAutoThreshold + 50;

        SourceIndexBuilder builder = new(
            bloomAllColumns: false,
            indexAllColumns: false,
            chunkSize: count,
            autoIndexColumns: true);

        TestTableProvider provider = new(
            ["id"],
            Enumerable.Range(0, count).Select(i =>
                new DataValue[] { DataValue.FromInt32(i) }));

        SourceIndex index = await builder.BuildAsync(
            TestTableDescriptor.Default, provider, sourceStream: null, fingerprint, CancellationToken.None);

        // High cardinality: bitmap should be abandoned.
        Assert.Null(index.BitmapIndexes);

        // Sorted index should exist.
        Assert.NotNull(index.SortedIndexes);
        Assert.True(index.SortedIndexes.HasColumn("id"));
    }

    /// <summary>
    /// Mixed table: one low-cardinality column (bitmap wins) and one high-cardinality
    /// column (sorted wins). Each should appear in exactly one index type.
    /// </summary>
    [Fact]
    public async Task BuildAsync_MixedCardinality_EachColumnInExactlyOneIndexType()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        string[] statuses = ["active", "inactive"];
        int count = 500;

        SourceIndexBuilder builder = new(
            bloomAllColumns: false,
            indexAllColumns: false,
            chunkSize: count,
            autoIndexColumns: true);

        TestTableProvider provider = new(
            ["status", "score"],
            Enumerable.Range(0, count).Select(i =>
                new DataValue[]
                {
                    DataValue.FromString(statuses[i % 2]),
                    DataValue.FromFloat32(i * 0.1f)
                }));

        SourceIndex index = await builder.BuildAsync(
            TestTableDescriptor.Default, provider, sourceStream: null, fingerprint, CancellationToken.None);

        // status: low cardinality → bitmap only.
        Assert.NotNull(index.BitmapIndexes);
        Assert.True(index.BitmapIndexes.TryGetIndex("status", out _));

        if (index.SortedIndexes is not null)
        {
            Assert.False(
                index.SortedIndexes.HasColumn("status"),
                "Low-cardinality 'status' should only be in bitmap, not sorted.");
        }

        // score: high cardinality → sorted only.
        Assert.NotNull(index.SortedIndexes);
        Assert.True(index.SortedIndexes.HasColumn("score"));
    }

    // ───────────────────────── Hint-Aware Gating ─────────────────────────

    /// <summary>
    /// When a column has a <see cref="IndexHintType.Bitmap"/> hint, only a bitmap index
    /// should be built — the column must not appear in the sorted index set.
    /// </summary>
    [Fact]
    public async Task BuildAsync_BitmapHint_ProducesBitmapOnly()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        string[] categories = ["electronics", "clothing", "food"];

        ColumnIndexHint[] hints = [new ColumnIndexHint("category", IndexHintType.Bitmap)];

        SourceIndexBuilder builder = new(
            bloomAllColumns: false,
            indexAllColumns: false,
            chunkSize: 100,
            autoIndexColumns: true,
            indexHints: hints);

        TestTableProvider provider = new(
            ["category"],
            Enumerable.Range(0, 300).Select(i =>
                new DataValue[] { DataValue.FromString(categories[i % 3]) }));

        SourceIndex index = await builder.BuildAsync(
            TestTableDescriptor.Default, provider, sourceStream: null, fingerprint, CancellationToken.None);

        Assert.NotNull(index.BitmapIndexes);
        Assert.True(index.BitmapIndexes.TryGetIndex("category", out _));

        // Bitmap hint must exclude this column from the sorted index set.
        Assert.Null(index.SortedIndexes);
    }

    /// <summary>
    /// When a column has a <see cref="IndexHintType.Sorted"/> hint, a sorted index should
    /// be built and no bitmap accumulator should be created for it.
    /// </summary>
    [Fact]
    public async Task BuildAsync_SortedHint_ProducesSortedOnly()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        string[] categories = ["electronics", "clothing", "food"];

        ColumnIndexHint[] hints = [new ColumnIndexHint("category", IndexHintType.Sorted)];

        SourceIndexBuilder builder = new(
            bloomAllColumns: false,
            indexAllColumns: false,
            chunkSize: 100,
            autoIndexColumns: true,
            indexHints: hints);

        TestTableProvider provider = new(
            ["category"],
            Enumerable.Range(0, 300).Select(i =>
                new DataValue[] { DataValue.FromString(categories[i % 3]) }));

        SourceIndex index = await builder.BuildAsync(
            TestTableDescriptor.Default, provider, sourceStream: null, fingerprint, CancellationToken.None);

        // Sorted hint → bitmap should not be built for this column.
        Assert.Null(index.BitmapIndexes);

        // Sorted index should contain the column.
        Assert.NotNull(index.SortedIndexes);
        Assert.True(index.SortedIndexes.HasColumn("category"));
    }

    /// <summary>
    /// When a column has a <see cref="IndexHintType.None"/> hint, no index of any type
    /// should be built for it.
    /// </summary>
    [Fact]
    public async Task BuildAsync_NoneHint_ProducesNoIndex()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        string[] categories = ["electronics", "clothing", "food"];

        ColumnIndexHint[] hints = [new ColumnIndexHint("category", IndexHintType.None)];

        SourceIndexBuilder builder = new(
            bloomAllColumns: false,
            indexAllColumns: false,
            chunkSize: 100,
            autoIndexColumns: true,
            indexHints: hints);

        TestTableProvider provider = new(
            ["category"],
            Enumerable.Range(0, 300).Select(i =>
                new DataValue[] { DataValue.FromString(categories[i % 3]) }));

        SourceIndex index = await builder.BuildAsync(
            TestTableDescriptor.Default, provider, sourceStream: null, fingerprint, CancellationToken.None);

        Assert.Null(index.BitmapIndexes);
        Assert.Null(index.SortedIndexes);
    }

    /// <summary>
    /// Verifies that the <see cref="IncrementalIndexBuilder"/> path respects manifest hints.
    /// A column hinted as Bitmap should produce bitmap only, verified via round-trip serialization.
    /// </summary>
    [Fact]
    public void IncrementalBuilder_BitmapHint_ProducesBitmapOnly()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        string[] categories = ["electronics", "clothing", "food"];

        ColumnIndexHint[] hints = [new ColumnIndexHint("category", IndexHintType.Bitmap)];

        SourceIndexBuilder builder = new(
            bloomAllColumns: false,
            indexAllColumns: false,
            chunkSize: 100,
            autoIndexColumns: true,
            indexHints: hints);

        IncrementalIndexBuilder incremental = builder.CreateIncrementalBuilder(fingerprint);

        for (int i = 0; i < 300; i++)
        {
            string[] names = ["category"];
            DataValue[] values = [DataValue.FromString(categories[i % 3])];
            incremental.AddRow(new Row(names, values));
        }

        SourceIndex index = incremental.Finalize();

        Assert.NotNull(index.BitmapIndexes);
        Assert.True(index.BitmapIndexes.TryGetIndex("category", out _));

        // Round-trip through serialization to verify no sorted/B+Tree entries leak through.
        SourceIndexSet indexSet = new(fingerprint, new Dictionary<string, SourceIndex> { ["test"] = index });

        string tempFile2 = Path.GetTempFileName();
        try
        {
            using (FileStream stream = File.Create(tempFile2))
            {
                UnifiedIndexWriter.Write(indexSet, stream, incremental.SpillWriter);
            }
            using MappedSourceIndexSet mapped = UnifiedIndexReader.Open(tempFile2);
            SourceIndex roundTripped = mapped.IndexSet.Tables["test"];

            Assert.NotNull(roundTripped.BitmapIndexes);
            Assert.True(roundTripped.BitmapIndexes.TryGetIndex("category", out _));
            Assert.Null(roundTripped.SortedIndexes);
            Assert.Null(roundTripped.BPlusTreeIndexes);

            incremental.Dispose();
        }
        finally
        {
            File.Delete(tempFile2);
        }
    }

    // ───────────────────────── Deferred Reindex ─────────────────────────

    /// <summary>
    /// When a column has a <see cref="IndexHintType.Bitmap"/> hint but its cardinality
    /// exceeds the bitmap threshold, the primary pass defers the column. The second pass
    /// should build a sorted index for it via auto-cascade.
    /// </summary>
    [Fact]
    public async Task BuildAsync_BitmapHintHighCardinality_DeferredToSortedIndex()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        int count = IndexConstants.BitmapAutoThreshold + 50;

        ColumnIndexHint[] hints = [new ColumnIndexHint("id", IndexHintType.Bitmap)];

        SourceIndexBuilder builder = new(
            bloomAllColumns: false,
            indexAllColumns: false,
            chunkSize: count,
            autoIndexColumns: true,
            indexHints: hints);

        TestTableProvider provider = new(
            ["id"],
            Enumerable.Range(0, count).Select(i =>
                new DataValue[] { DataValue.FromInt32(i) }));

        SourceIndex index = await builder.BuildAsync(
            TestTableDescriptor.Default, provider, sourceStream: null, fingerprint, CancellationToken.None);

        // Bitmap hint failed (cardinality > 256), so bitmap should be absent for this column.
        Assert.Null(index.BitmapIndexes);

        // The deferred second pass should have built a sorted index.
        Assert.NotNull(index.SortedIndexes);
        Assert.True(index.SortedIndexes.HasColumn("id"));
    }

    /// <summary>
    /// Verifies that the <see cref="IncrementalIndexBuilder"/> tracks deferred columns
    /// when a bitmap hint fails due to high cardinality.
    /// </summary>
    [Fact]
    public void IncrementalBuilder_BitmapHintHighCardinality_TracksDeferredColumn()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        int count = IndexConstants.BitmapAutoThreshold + 50;

        ColumnIndexHint[] hints = [new ColumnIndexHint("id", IndexHintType.Bitmap)];

        SourceIndexBuilder builder = new(
            bloomAllColumns: false,
            indexAllColumns: false,
            chunkSize: count,
            autoIndexColumns: true,
            indexHints: hints);

        IncrementalIndexBuilder incremental = builder.CreateIncrementalBuilder(fingerprint);

        for (int i = 0; i < count; i++)
        {
            string[] names = ["id"];
            DataValue[] values = [DataValue.FromInt32(i)];
            incremental.AddRow(new Row(names, values));
        }

        SourceIndex index = incremental.Finalize();

        // IncrementalBuilder cannot do a second pass, but should report the deferred column.
        Assert.Single(incremental.DeferredReindexColumns);
        Assert.Equal("id", incremental.DeferredReindexColumns[0]);

        incremental.Dispose();
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static class TestTableDescriptor
    {
        internal static readonly TableDescriptor Default = new("csv", "test", "test.csv",
            new Dictionary<string, string>());
    }

    private sealed class TestTableProvider : ITableProvider
    {
        private readonly string[] _columnNames;
        private readonly IEnumerable<DataValue[]> _rows;

        internal TestTableProvider(string[] columnNames, IEnumerable<DataValue[]> rows)
        {
            _columnNames = columnNames;
            _rows = rows;
        }

        public Task<Schema> GetSchemaAsync(TableDescriptor descriptor, CancellationToken cancellationToken)
        {
            ColumnInfo[] columns = _columnNames.Select(name => new ColumnInfo(name, DataKind.String, true)).ToArray();
            return Task.FromResult(new Schema(columns));
        }

        public Task<ProviderCapabilities> GetCapabilitiesAsync(
            TableDescriptor descriptor, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ProviderCapabilities(
                EstimatedRowCount: null,
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

            foreach (DataValue[] values in _rows)
            {
                batch.Add(new Row(_columnNames, values));

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
