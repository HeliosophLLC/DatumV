using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Indexing;
using DatumIngest.Indexing.Bitmap;
using DatumIngest.Model;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// Tests that <see cref="SourceIndexBuilder"/> and <see cref="IncrementalIndexBuilder"/>
/// automatically accumulate bitmap indexes for low-cardinality auto-indexable columns
/// and abandon tracking when cardinality exceeds the threshold.
/// </summary>
public sealed class BitmapBuildPipelineTests
{
    // ───────────────────────── SourceIndexBuilder ─────────────────────────

    [Fact]
    public async Task BuildAsync_BooleanColumn_ProducesBitmapIndex()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);

        SourceIndexBuilder builder = new(chunkSize: 10);
        InMemoryTableProvider provider = new(
            ["flag"],
            Enumerable.Range(0, 10).Select(i => new Row(["flag"], [DataValue.FromBoolean(i % 2 == 0)])).ToArray());

        SourceIndex index = await builder.BuildAsync(
            TestTableDescriptor.Default, provider, sourceStream: null, fingerprint, CancellationToken.None);

        Assert.NotNull(index.BitmapIndexes);
        Assert.True(index.BitmapIndexes.TryGetIndex("flag", out BitmapColumnIndex? bitmapIndex));
        Assert.Equal(2, bitmapIndex.DistinctValues.Count);

        ChunkBitmap trueBitmap = bitmapIndex.GetChunkBitmap(DataValue.FromBoolean(true), 0);
        Assert.Equal(5, trueBitmap.PopCount());
    }

    [Fact]
    public async Task BuildAsync_StringColumn_LowCardinality_ProducesBitmapIndex()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        string[] colors = ["red", "green", "blue"];

        SourceIndexBuilder builder = new(chunkSize: 9);
        InMemoryTableProvider provider = new(
            ["color"],
            Enumerable.Range(0, 9).Select(i => new Row(["color"], [DataValue.FromString(colors[i % 3])])).ToArray());

        SourceIndex index = await builder.BuildAsync(
            TestTableDescriptor.Default, provider, sourceStream: null, fingerprint, CancellationToken.None);

        Assert.NotNull(index.BitmapIndexes);
        Assert.True(index.BitmapIndexes.TryGetIndex("color", out BitmapColumnIndex? bitmapIndex));
        Assert.Equal(3, bitmapIndex.DistinctValues.Count);

        ChunkBitmap redBitmap = bitmapIndex.GetChunkBitmap(DataValue.FromString("red"), 0);
        Assert.Equal(3, redBitmap.PopCount());
    }

    [Fact]
    public async Task BuildAsync_HighCardinality_AbandonsBitmap()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        int count = IndexConstants.BitmapAutoThreshold + 10;

        SourceIndexBuilder builder = new(chunkSize: count);
        InMemoryTableProvider provider = new(
            ["id"],
            Enumerable.Range(0, count).Select(i => new Row(["id"], [DataValue.FromFloat32((float)i)])).ToArray());

        SourceIndex index = await builder.BuildAsync(
            TestTableDescriptor.Default, provider, sourceStream: null, fingerprint, CancellationToken.None);

        // Should be null because cardinality exceeded the threshold.
        Assert.Null(index.BitmapIndexes);
    }

    [Fact]
    public async Task BuildAsync_MultipleChunks_PreservesChunkBitmaps()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);

        // 6 rows, chunkSize=3 → 2 chunks.
        SourceIndexBuilder builder = new(chunkSize: 3);
        InMemoryTableProvider provider = new(
            ["kind"],
            [
                new Row(["kind"], [DataValue.FromString("A")]),
                new Row(["kind"], [DataValue.FromString("B")]),
                new Row(["kind"], [DataValue.FromString("A")]),
                new Row(["kind"], [DataValue.FromString("B")]),
                new Row(["kind"], [DataValue.FromString("A")]),
                new Row(["kind"], [DataValue.FromString("B")]),
            ]);

        SourceIndex index = await builder.BuildAsync(
            TestTableDescriptor.Default, provider, sourceStream: null, fingerprint, CancellationToken.None);

        Assert.NotNull(index.BitmapIndexes);
        Assert.True(index.BitmapIndexes.TryGetIndex("kind", out BitmapColumnIndex? bitmapIndex));
        Assert.Equal(2, bitmapIndex.ChunkCount);

        // Chunk 0: A at rows 0,2; B at row 1.
        ChunkBitmap aChunk0 = bitmapIndex.GetChunkBitmap(DataValue.FromString("A"), 0);
        Assert.Equal(2, aChunk0.PopCount());
        Assert.True(aChunk0.IsSet(0));
        Assert.True(aChunk0.IsSet(2));

        ChunkBitmap bChunk0 = bitmapIndex.GetChunkBitmap(DataValue.FromString("B"), 0);
        Assert.Equal(1, bChunk0.PopCount());
        Assert.True(bChunk0.IsSet(1));

        // Chunk 1: B at rows 0,2; A at row 1.
        ChunkBitmap aChunk1 = bitmapIndex.GetChunkBitmap(DataValue.FromString("A"), 1);
        Assert.Equal(1, aChunk1.PopCount());
        Assert.True(aChunk1.IsSet(1));
    }

    [Fact]
    public async Task BuildAsync_NonIndexableKind_NoBitmap()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);

        SourceIndexBuilder builder = new(chunkSize: 10);
        InMemoryTableProvider provider = new(
            ["data"],
            Enumerable.Range(0, 5).Select(i => new Row(["data"], [DataValue.FromUInt8Array(new byte[] { (byte)i })])).ToArray());

        SourceIndex index = await builder.BuildAsync(
            TestTableDescriptor.Default, provider, sourceStream: null, fingerprint, CancellationToken.None);

        // UInt8Array is not auto-indexable, so no bitmap.
        Assert.Null(index.BitmapIndexes);
    }

    [Fact]
    public async Task BuildAsync_NullValues_SkippedByBitmap()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);

        SourceIndexBuilder builder = new(chunkSize: 10);
        InMemoryTableProvider provider = new(
            ["status"],
            [
                new Row(["status"], [DataValue.FromString("active")]),
                new Row(["status"], [DataValue.Null(DataKind.String)]),
                new Row(["status"], [DataValue.FromString("active")]),
                new Row(["status"], [DataValue.Null(DataKind.String)]),
            ]);

        SourceIndex index = await builder.BuildAsync(
            TestTableDescriptor.Default, provider, sourceStream: null, fingerprint, CancellationToken.None);

        Assert.NotNull(index.BitmapIndexes);
        Assert.True(index.BitmapIndexes.TryGetIndex("status", out BitmapColumnIndex? bitmapIndex));
        Assert.Single(bitmapIndex.DistinctValues); // Only "active", not null.

        ChunkBitmap activeBitmap = bitmapIndex.GetChunkBitmap(DataValue.FromString("active"), 0);
        Assert.Equal(2, activeBitmap.PopCount());
        Assert.True(activeBitmap.IsSet(0));
        Assert.True(activeBitmap.IsSet(2));
    }

    [Fact]
    public async Task BuildAsync_LastPartialChunk_HasCorrectRowCount()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);

        // 5 rows, chunkSize=3 → chunk0 (3 rows), chunk1 (2 rows).
        SourceIndexBuilder builder = new(chunkSize: 3);
        InMemoryTableProvider provider = new(
            ["val"],
            Enumerable.Range(0, 5)
            .Select(i => new Row(["val"], [DataValue.FromBoolean(true)]))
            .ToArray());

        SourceIndex index = await builder.BuildAsync(
            TestTableDescriptor.Default, provider, sourceStream: null, fingerprint, CancellationToken.None);

        Assert.NotNull(index.BitmapIndexes);
        Assert.True(index.BitmapIndexes.TryGetIndex("val", out BitmapColumnIndex? bitmapIndex));
        Assert.Equal(2, bitmapIndex.ChunkCount);

        ChunkBitmap chunk1 = bitmapIndex.GetChunkBitmap(DataValue.FromBoolean(true), 1);
        Assert.Equal(2, chunk1.RowCount);
        Assert.Equal(2, chunk1.PopCount());
    }

    // ───────────────────────── IncrementalIndexBuilder ─────────────────────────

    [Fact]
    public void IncrementalBuilder_BooleanColumn_ProducesBitmapIndex()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        SourceIndexBuilder builder = new(chunkSize: 10);
        IncrementalIndexBuilder incremental = builder.CreateIncrementalBuilder(fingerprint);

        for (int i = 0; i < 10; i++)
        {
            string[] names = ["flag"];
            DataValue[] values = [DataValue.FromBoolean(i % 2 == 0)];
            incremental.AddRow(new Row(names, values));
        }

        SourceIndex index = incremental.Finalize();
        incremental.Dispose();

        Assert.NotNull(index.BitmapIndexes);
        Assert.True(index.BitmapIndexes.TryGetIndex("flag", out BitmapColumnIndex? bitmapIndex));
        Assert.Equal(2, bitmapIndex.DistinctValues.Count);
    }

    [Fact]
    public void IncrementalBuilder_MultipleChunks_PreservesBitmaps()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        SourceIndexBuilder builder = new(chunkSize: 3);
        IncrementalIndexBuilder incremental = builder.CreateIncrementalBuilder(fingerprint);

        string[] categories = ["A", "B"];

        for (int i = 0; i < 6; i++)
        {
            string[] names = ["cat"];
            DataValue[] values = [DataValue.FromString(categories[i % 2])];
            incremental.AddRow(new Row(names, values));
        }

        SourceIndex index = incremental.Finalize();
        incremental.Dispose();

        Assert.NotNull(index.BitmapIndexes);
        Assert.True(index.BitmapIndexes.TryGetIndex("cat", out BitmapColumnIndex? bitmapIndex));
        Assert.Equal(2, bitmapIndex.ChunkCount);
        Assert.Equal(2, bitmapIndex.DistinctValues.Count);

        // Chunk 0: A at 0,2; B at 1.
        ChunkBitmap aChunk0 = bitmapIndex.GetChunkBitmap(DataValue.FromString("A"), 0);
        Assert.Equal(2, aChunk0.PopCount());
    }

    [Fact]
    public void IncrementalBuilder_HighCardinality_AbandonsBitmap()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        int count = IndexConstants.BitmapAutoThreshold + 10;

        SourceIndexBuilder builder = new(chunkSize: count);
        IncrementalIndexBuilder incremental = builder.CreateIncrementalBuilder(fingerprint);

        for (int i = 0; i < count; i++)
        {
            string[] names = ["id"];
            DataValue[] values = [DataValue.FromFloat32((float)i)];
            incremental.AddRow(new Row(names, values));
        }

        SourceIndex index = incremental.Finalize();
        incremental.Dispose();

        Assert.Null(index.BitmapIndexes);
    }

    // ───────────────────────── End-to-end round-trip ─────────────────────────

    [Fact]
    public void BuildAndSerialize_BitmapSurvivesRoundTrip()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        SourceIndexBuilder builder = new(chunkSize: 5);
        IncrementalIndexBuilder incremental = builder.CreateIncrementalBuilder(fingerprint);

        for (int i = 0; i < 5; i++)
        {
            string[] names = ["color"];
            DataValue[] values = [DataValue.FromString(i < 3 ? "red" : "blue")];
            incremental.AddRow(new Row(names, values));
        }

        SourceIndex index = incremental.Finalize();

        // Write and read back via the v5 unified format.
        string tempFile = Path.GetTempFileName();
        try
        {
            using (FileStream stream = File.Create(tempFile))
            {
                SourceIndexSet indexSet = SourceIndexSet.Create("test", index);
                UnifiedIndexWriter.Write(indexSet, stream, incremental.SpillWriter);
            }
            using MappedSourceIndexSet mapped = UnifiedIndexReader.Open(tempFile);
            SourceIndex restored = mapped.IndexSet.Tables["test"];

            Assert.NotNull(restored.BitmapIndexes);
            Assert.True(restored.BitmapIndexes.TryGetIndex("color", out BitmapColumnIndex? bitmapIndex));
            Assert.Equal(2, bitmapIndex.DistinctValues.Count);

            ChunkBitmap redBitmap = bitmapIndex.GetChunkBitmap(DataValue.FromString("red"), 0);
            Assert.Equal(3, redBitmap.PopCount());
            Assert.True(redBitmap.IsSet(0));
            Assert.True(redBitmap.IsSet(1));
            Assert.True(redBitmap.IsSet(2));

            incremental.Dispose();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // ───────────────────────── Bitmap coexists with sorted ─────────────────────────

    [Fact]
    public void BuildAndSerialize_BitmapAndSortedCoexist()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        HashSet<string> indexColumns = new(StringComparer.OrdinalIgnoreCase) { "color" };
        SourceIndexBuilder builder = new(chunkSize: 5, indexColumns: indexColumns);
        IncrementalIndexBuilder incremental = builder.CreateIncrementalBuilder(fingerprint);

        for (int i = 0; i < 5; i++)
        {
            string[] names = ["color"];
            DataValue[] values = [DataValue.FromString(i < 3 ? "red" : "blue")];
            incremental.AddRow(new Row(names, values));
        }

        SourceIndex index = incremental.Finalize();

        string tempFile = Path.GetTempFileName();
        try
        {
            using (FileStream stream = File.Create(tempFile))
            {
                SourceIndexSet indexSet = SourceIndexSet.Create("test", index);
                UnifiedIndexWriter.Write(indexSet, stream, incremental.SpillWriter);
            }
            using MappedSourceIndexSet mapped = UnifiedIndexReader.Open(tempFile);
            SourceIndex restored = mapped.IndexSet.Tables["test"];

            // Both types should be present.
            Assert.NotNull(restored.BitmapIndexes);
            Assert.NotNull(restored.MappedSortedIndexes);
            Assert.True(restored.BitmapIndexes.TryGetIndex("color", out _));
            Assert.True(restored.MappedSortedIndexes.ContainsKey("color"));

            incremental.Dispose();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // ───────────────────────── Helpers ─────────────────────────

    /// <summary>Minimal table descriptor for test purposes.</summary>
    private static class TestTableDescriptor
    {
        internal static readonly TableDescriptor Default = new("csv", "test", "test.csv",
            new Dictionary<string, string>());
    }
}