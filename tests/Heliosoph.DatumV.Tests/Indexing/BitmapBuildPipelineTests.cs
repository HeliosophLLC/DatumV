using Heliosoph.DatumV.Indexing;
using Heliosoph.DatumV.Indexing.Bitmap;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Indexing;

/// <summary>
/// Tests that <see cref="SourceIndexBuilder"/> and <see cref="IncrementalIndexBuilder"/>
/// automatically accumulate bitmap indexes for low-cardinality auto-indexable columns
/// and abandon tracking when cardinality exceeds the threshold.
/// </summary>
public sealed class BitmapBuildPipelineTests : ServiceTestBase
{
    private readonly Arena _store;

    public BitmapBuildPipelineTests()
    {
        _store = CreateArena();
    }

    // ───────────────────────── IncrementalIndexBuilder ─────────────────────────

    [Fact]
    public void IncrementalBuilder_BooleanColumn_ProducesBitmapIndex()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        SourceIndexBuilder builder = new(chunkSize: 10);
        IncrementalIndexBuilder incremental = builder.CreateIncrementalBuilder(fingerprint);

        ColumnLookup lookup = new(["flag"]);

        for (int i = 0; i < 10; i++)
        {
            DataValue[] values = [DataValue.FromBoolean(i % 2 == 0)];
            incremental.AddRow(MakeRow(lookup, values), _store);
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

        ColumnLookup lookup = new(["cat"]);

        string[] categories = ["A", "B"];

        for (int i = 0; i < 6; i++)
        {
            DataValue[] values = [DataValue.FromString(categories[i % 2])];
            incremental.AddRow(MakeRow(lookup, values), _store);
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

        ColumnLookup lookup = new(["id"]);

        for (int i = 0; i < count; i++)
        {
            DataValue[] values = [DataValue.FromFloat32((float)i)];
            incremental.AddRow(MakeRow(lookup, values), _store);
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

        ColumnLookup lookup = new(["color"]);

        for (int i = 0; i < 5; i++)
        {
            DataValue[] values = [DataValue.FromString(i < 3 ? "red" : "blue")];
            incremental.AddRow(MakeRow(lookup, values), _store);
        }

        SourceIndex index = incremental.Finalize();

        // Write and read back via the v5 unified format.
        string tempFile = Path.GetTempFileName();
        try
        {
            using (FileStream stream = File.Create(tempFile))
            {
                SourceIndexSet indexSet = SourceIndexSet.Create("test", index);
                UnifiedIndexWriter.Write(indexSet, stream);
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

    // PR13d (v8): the bitmap-and-sorted-coexist test was retired with the
    // SortedIndexes section. SortedIndex acceleration is no longer carried
    // in the unified sidecar — bitmap is the only acceleration this builder
    // produces. Coexistence with B+Tree per-column files is exercised in the
    // provider tests once Commit 2b lands.

}