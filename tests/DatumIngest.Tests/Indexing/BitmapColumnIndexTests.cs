using DatumIngest.Indexing.Bitmap;
using DatumIngest.Model;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// Tests for <see cref="BitmapColumnIndex"/> — multi-value, multi-chunk bitmap index
/// with on-demand decompression, chunk containment checks, and chunk finding.
/// </summary>
public sealed class BitmapColumnIndexTests : ServiceTestBase
{
    // ───────────────────────── GetChunkBitmap ─────────────────────────

    [Fact]
    public void GetChunkBitmap_ExistingValueAndChunk_ReturnsDecompressedBitmap()
    {
        BitmapColumnIndex index = BuildTwoValueIndex();

        ChunkBitmap bitmap = index.GetChunkBitmap(DataValue.FromString("red"), chunkIndex: 0);

        Assert.Equal(10, bitmap.RowCount);
        Assert.True(bitmap.IsSet(0));
        Assert.True(bitmap.IsSet(5));
        Assert.False(bitmap.IsSet(1));
    }

    [Fact]
    public void GetChunkBitmap_MissingValue_ReturnsEmptyBitmap()
    {
        BitmapColumnIndex index = BuildTwoValueIndex();

        ChunkBitmap bitmap = index.GetChunkBitmap(DataValue.FromString("green"), chunkIndex: 0);

        Assert.True(bitmap.IsEmpty);
        Assert.Equal(10, bitmap.RowCount);
    }

    [Fact]
    public void GetChunkBitmap_InvalidChunkIndex_ReturnsEmptyBitmap()
    {
        BitmapColumnIndex index = BuildTwoValueIndex();

        ChunkBitmap bitmap = index.GetChunkBitmap(DataValue.FromString("red"), chunkIndex: 99);

        Assert.True(bitmap.IsEmpty);
        Assert.Equal(0, bitmap.RowCount);
    }

    [Fact]
    public void GetChunkBitmap_ValueAbsentFromChunk_ReturnsEmptyBitmap()
    {
        BitmapColumnIndex index = BuildTwoValueIndex();

        // "blue" has no entries in chunk 0 (empty compressed payload).
        ChunkBitmap bitmap = index.GetChunkBitmap(DataValue.FromString("blue"), chunkIndex: 0);

        Assert.True(bitmap.IsEmpty);
    }

    // ───────────────────────── ChunkContainsValue ─────────────────────────

    [Fact]
    public void ChunkContainsValue_PresentValue_ReturnsTrue()
    {
        BitmapColumnIndex index = BuildTwoValueIndex();

        Assert.True(index.ChunkContainsValue(DataValue.FromString("red"), chunkIndex: 0));
    }

    [Fact]
    public void ChunkContainsValue_AbsentValue_ReturnsFalse()
    {
        BitmapColumnIndex index = BuildTwoValueIndex();

        Assert.False(index.ChunkContainsValue(DataValue.FromString("green"), chunkIndex: 0));
    }

    [Fact]
    public void ChunkContainsValue_EmptyCompressedPayload_ReturnsFalse()
    {
        BitmapColumnIndex index = BuildTwoValueIndex();

        Assert.False(index.ChunkContainsValue(DataValue.FromString("blue"), chunkIndex: 0));
    }

    [Fact]
    public void ChunkContainsValue_InvalidChunkIndex_ReturnsFalse()
    {
        BitmapColumnIndex index = BuildTwoValueIndex();

        Assert.False(index.ChunkContainsValue(DataValue.FromString("red"), chunkIndex: 99));
    }

    // ───────────────────────── FindChunksContaining ─────────────────────────

    [Fact]
    public void FindChunksContaining_ValueInMultipleChunks_ReturnsAll()
    {
        BitmapColumnIndex index = BuildTwoValueIndex();

        IReadOnlySet<int> chunks = index.FindChunksContaining(DataValue.FromString("red"));

        Assert.Equal(2, chunks.Count);
        Assert.Contains(0, chunks);
        Assert.Contains(1, chunks);
    }

    [Fact]
    public void FindChunksContaining_ValueInOneChunk_ReturnsSingleChunk()
    {
        BitmapColumnIndex index = BuildTwoValueIndex();

        IReadOnlySet<int> chunks = index.FindChunksContaining(DataValue.FromString("blue"));

        Assert.Single(chunks);
        Assert.Contains(1, chunks);
    }

    [Fact]
    public void FindChunksContaining_MissingValue_ReturnsEmpty()
    {
        BitmapColumnIndex index = BuildTwoValueIndex();

        IReadOnlySet<int> chunks = index.FindChunksContaining(DataValue.FromString("green"));

        Assert.Empty(chunks);
    }

    // ───────────────────────── DistinctValues / ChunkCount ─────────────────────────

    [Fact]
    public void DistinctValues_ReturnsAllValues()
    {
        BitmapColumnIndex index = BuildTwoValueIndex();

        Assert.Equal(2, index.DistinctValues.Count);
        Assert.Contains(DataValue.FromString("red"), index.DistinctValues);
        Assert.Contains(DataValue.FromString("blue"), index.DistinctValues);
    }

    [Fact]
    public void ChunkCount_ReturnsCorrectCount()
    {
        BitmapColumnIndex index = BuildTwoValueIndex();

        Assert.Equal(2, index.ChunkCount);
    }

    // ───────────────────────── Boolean column ─────────────────────────

    [Fact]
    public void BooleanColumn_TwoValues_WorksCorrectly()
    {
        // Simulate a "reordered" boolean column with 2 chunks of 8 rows each.
        int rowCount = 8;
        int chunkCount = 2;
        int[] chunkRowCounts = [rowCount, rowCount];

        ChunkBitmap trueChunk0 = ChunkBitmap.Create(rowCount);
        trueChunk0.SetBit(0);
        trueChunk0.SetBit(3);
        trueChunk0.SetBit(7);

        ChunkBitmap trueChunk1 = ChunkBitmap.Create(rowCount);
        trueChunk1.SetBit(1);
        trueChunk1.SetBit(4);

        ChunkBitmap falseChunk0 = ChunkBitmap.Create(rowCount);
        falseChunk0.SetBit(1);
        falseChunk0.SetBit(2);
        falseChunk0.SetBit(4);
        falseChunk0.SetBit(5);
        falseChunk0.SetBit(6);

        ChunkBitmap falseChunk1 = ChunkBitmap.Create(rowCount);
        falseChunk1.SetBit(0);
        falseChunk1.SetBit(2);
        falseChunk1.SetBit(3);
        falseChunk1.SetBit(5);
        falseChunk1.SetBit(6);
        falseChunk1.SetBit(7);

        DataValue trueValue = DataValue.FromBoolean(true);
        DataValue falseValue = DataValue.FromBoolean(false);

        Dictionary<DataValue, byte[][]> compressedBitmaps = new()
        {
            [trueValue] = [trueChunk0.Compress(), trueChunk1.Compress()],
            [falseValue] = [falseChunk0.Compress(), falseChunk1.Compress()],
        };

        BitmapColumnIndex index = new(compressedBitmaps, chunkCount, chunkRowCounts);

        // Both values appear in both chunks.
        Assert.Equal(2, index.FindChunksContaining(trueValue).Count);
        Assert.Equal(2, index.FindChunksContaining(falseValue).Count);

        // Verify the true bitmap for chunk 0.
        ChunkBitmap retrieved = index.GetChunkBitmap(trueValue, 0);
        Assert.Equal(3, retrieved.PopCount());
        Assert.True(retrieved.IsSet(0));
        Assert.True(retrieved.IsSet(3));
        Assert.True(retrieved.IsSet(7));
    }

    // ───────────────────── Cross-scope resilience ─────────────────────

    [Fact]
    public void GetChunkBitmap_StringKey_SurvivesScopeChange()
    {
        // Build the index, then look up values using a fresh Arena.
        BitmapColumnIndex index = BuildTwoValueIndex();

        using Arena arena = new();
        DataValue red = DataValue.FromString("red", arena);

        ChunkBitmap bitmap = index.GetChunkBitmap(red, chunkIndex: 0);

        Assert.Equal(10, bitmap.RowCount);
        Assert.True(bitmap.IsSet(0));
        Assert.True(bitmap.IsSet(5));
        Assert.False(bitmap.IsSet(1));
    }

    [Fact]
    public void ChunkContainsValue_StringKey_SurvivesScopeChange()
    {
        BitmapColumnIndex index = BuildTwoValueIndex();
        using Arena arena = new();

        Assert.True(index.ChunkContainsValue(DataValue.FromString("red", arena), chunkIndex: 0));
        Assert.False(index.ChunkContainsValue(DataValue.FromString("green", arena), chunkIndex: 0));
        Assert.False(index.ChunkContainsValue(DataValue.FromString("blue", arena), chunkIndex: 0));
        Assert.True(index.ChunkContainsValue(DataValue.FromString("blue", arena), chunkIndex: 1));
    }

    [Fact]
    public void FindChunksContaining_StringKey_SurvivesScopeChange()
    {
        BitmapColumnIndex index = BuildTwoValueIndex();
        using Arena arena = new();

        IReadOnlySet<int> redChunks = index.FindChunksContaining(DataValue.FromString("red", arena));
        Assert.Equal(2, redChunks.Count);
        Assert.Contains(0, redChunks);
        Assert.Contains(1, redChunks);

        IReadOnlySet<int> blueChunks = index.FindChunksContaining(DataValue.FromString("blue", arena));
        Assert.Single(blueChunks);
        Assert.Contains(1, blueChunks);

        IReadOnlySet<int> greenChunks = index.FindChunksContaining(DataValue.FromString("green", arena));
        Assert.Empty(greenChunks);
    }

    // ───────────────────────── Helpers ─────────────────────────

    /// <summary>
    /// Builds a bitmap column index with two values ("red", "blue") across 2 chunks
    /// of 10 rows each. "red" appears in both chunks, "blue" only in chunk 1.
    /// </summary>
    private static BitmapColumnIndex BuildTwoValueIndex()
    {
        int rowCount = 10;
        int chunkCount = 2;
        int[] chunkRowCounts = [rowCount, rowCount];

        // "red" in chunk 0: rows 0, 5.
        ChunkBitmap redChunk0 = ChunkBitmap.Create(rowCount);
        redChunk0.SetBit(0);
        redChunk0.SetBit(5);

        // "red" in chunk 1: rows 2, 9.
        ChunkBitmap redChunk1 = ChunkBitmap.Create(rowCount);
        redChunk1.SetBit(2);
        redChunk1.SetBit(9);

        // "blue" not in chunk 0, in chunk 1: rows 1, 6.
        ChunkBitmap blueChunk1 = ChunkBitmap.Create(rowCount);
        blueChunk1.SetBit(1);
        blueChunk1.SetBit(6);

        DataValue red = DataValue.FromString("red");
        DataValue blue = DataValue.FromString("blue");

        Dictionary<DataValue, byte[][]> compressedBitmaps = new()
        {
            [red] = [redChunk0.Compress(), redChunk1.Compress()],
            [blue] = [Array.Empty<byte>(), blueChunk1.Compress()],
        };

        return new BitmapColumnIndex(compressedBitmaps, chunkCount, chunkRowCounts);
    }
}
