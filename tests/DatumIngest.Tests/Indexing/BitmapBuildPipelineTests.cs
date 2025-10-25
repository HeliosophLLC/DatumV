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
    private static readonly Arena Store = new();


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
            incremental.AddRow(new Row(names, values), Store);
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
            incremental.AddRow(new Row(names, values), Store);
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
            incremental.AddRow(new Row(names, values), Store);
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
            incremental.AddRow(new Row(names, values), Store);
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
            incremental.AddRow(new Row(names, values), Store);
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

}