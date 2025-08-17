using DatumIngest.Indexing;
using DatumIngest.Indexing.Bitmap;
using DatumIngest.Model;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// End-to-end round-trip tests for bitmap indexes through the
/// <see cref="IndexWriter"/> → <see cref="IndexReader"/> pipeline.
/// Verifies that bitmap sections survive serialization and remain
/// queryable after deserialization.
/// </summary>
public sealed class BitmapSerializationTests
{
    // ───────────────────────── Single column, single value ─────────────────────────

    [Fact]
    public void RoundTrip_SingleColumn_SingleValue_PreservesBitmaps()
    {
        BitmapColumnIndex original = BuildSingleValueIndex(
            DataValue.FromBoolean(true), rowCount: 10, chunkCount: 1, setRows: [0, 3, 7]);

        SourceIndex restored = WriteAndRead(BuildSourceIndex("reordered", original));

        Assert.NotNull(restored.BitmapIndexes);
        Assert.Equal(1, restored.BitmapIndexes.Count);

        bool found = restored.BitmapIndexes.TryGetIndex("reordered", out BitmapColumnIndex? restoredIndex);
        Assert.True(found);
        Assert.NotNull(restoredIndex);
        Assert.Single(restoredIndex.DistinctValues);
        Assert.Equal(1, restoredIndex.ChunkCount);

        ChunkBitmap bitmap = restoredIndex.GetChunkBitmap(DataValue.FromBoolean(true), 0);
        Assert.Equal(3, bitmap.PopCount());
        Assert.True(bitmap.IsSet(0));
        Assert.True(bitmap.IsSet(3));
        Assert.True(bitmap.IsSet(7));
        Assert.False(bitmap.IsSet(1));
    }

    // ───────────────────────── Multiple values ─────────────────────────

    [Fact]
    public void RoundTrip_MultipleValues_PreservesAll()
    {
        int rowCount = 8;
        int chunkCount = 1;

        BitmapColumnIndex original = BuildMultiValueIndex(rowCount, chunkCount);

        SourceIndex restored = WriteAndRead(BuildSourceIndex("color", original));

        Assert.NotNull(restored.BitmapIndexes);

        bool found = restored.BitmapIndexes.TryGetIndex("color", out BitmapColumnIndex? restoredIndex);
        Assert.True(found);
        Assert.NotNull(restoredIndex);
        Assert.Equal(3, restoredIndex.DistinctValues.Count);

        // Verify "red" bitmap.
        ChunkBitmap redBitmap = restoredIndex.GetChunkBitmap(DataValue.FromString("red"), 0);
        Assert.Equal(2, redBitmap.PopCount());
        Assert.True(redBitmap.IsSet(0));
        Assert.True(redBitmap.IsSet(3));

        // Verify "blue" bitmap.
        ChunkBitmap blueBitmap = restoredIndex.GetChunkBitmap(DataValue.FromString("blue"), 0);
        Assert.Equal(2, blueBitmap.PopCount());
        Assert.True(blueBitmap.IsSet(1));
        Assert.True(blueBitmap.IsSet(4));

        // Verify "green" bitmap.
        ChunkBitmap greenBitmap = restoredIndex.GetChunkBitmap(DataValue.FromString("green"), 0);
        Assert.Equal(1, greenBitmap.PopCount());
        Assert.True(greenBitmap.IsSet(7));
    }

    // ───────────────────────── Multiple chunks ─────────────────────────

    [Fact]
    public void RoundTrip_MultipleChunks_PreservesChunkRowCounts()
    {
        int chunkCount = 3;
        int[] chunkRowCounts = [100, 100, 50]; // Last chunk smaller (typical).

        Dictionary<DataValue, byte[][]> compressedBitmaps = new();

        DataValue value = DataValue.FromFloat32(1.0f);
        byte[][] chunks = new byte[chunkCount][];

        for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            ChunkBitmap bitmap = ChunkBitmap.Create(chunkRowCounts[chunkIndex]);
            bitmap.SetBit(0);
            chunks[chunkIndex] = bitmap.Compress();
        }

        compressedBitmaps[value] = chunks;

        BitmapColumnIndex original = new(compressedBitmaps, chunkCount, chunkRowCounts);
        SourceIndex restored = WriteAndRead(BuildSourceIndex("category", original));

        Assert.NotNull(restored.BitmapIndexes);
        Assert.True(restored.BitmapIndexes.TryGetIndex("category", out BitmapColumnIndex? restoredIndex));

        Assert.Equal(3, restoredIndex.ChunkCount);

        // Verify chunk 2 has correct row count (50, not 100).
        ChunkBitmap chunk2 = restoredIndex.GetChunkBitmap(value, 2);
        Assert.Equal(50, chunk2.RowCount);
        Assert.True(chunk2.IsSet(0));
    }

    // ───────────────────────── Multiple columns ─────────────────────────

    [Fact]
    public void RoundTrip_MultipleColumns_PreservesAll()
    {
        int rowCount = 8;

        BitmapColumnIndex colorIndex = BuildMultiValueIndex(rowCount, chunkCount: 1);
        BitmapColumnIndex sizeIndex = BuildSingleValueIndex(
            DataValue.FromString("large"), rowCount, chunkCount: 1, setRows: [2, 5]);

        Dictionary<string, BitmapColumnIndex> indexes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["color"] = colorIndex,
            ["size"] = sizeIndex,
        };

        BitmapIndexSet bitmapSet = new(indexes);
        SourceIndex sourceIndex = BuildSourceIndexWithBitmapSet(bitmapSet);
        SourceIndex restored = WriteAndRead(sourceIndex);

        Assert.NotNull(restored.BitmapIndexes);
        Assert.Equal(2, restored.BitmapIndexes.Count);

        Assert.True(restored.BitmapIndexes.TryGetIndex("color", out _));
        Assert.True(restored.BitmapIndexes.TryGetIndex("size", out BitmapColumnIndex? sizeRestored));

        ChunkBitmap sizeBitmap = sizeRestored.GetChunkBitmap(DataValue.FromString("large"), 0);
        Assert.Equal(2, sizeBitmap.PopCount());
        Assert.True(sizeBitmap.IsSet(2));
        Assert.True(sizeBitmap.IsSet(5));
    }

    // ───────────────────────── FindChunksContaining after round-trip ─────────────────────────

    [Fact]
    public void RoundTrip_FindChunksContaining_WorksAfterDeserialization()
    {
        int chunkCount = 3;
        int rowCount = 10;
        int[] chunkRowCounts = [rowCount, rowCount, rowCount];

        // Value present in chunks 0 and 2 only.
        ChunkBitmap chunk0 = ChunkBitmap.Create(rowCount);
        chunk0.SetBit(5);

        ChunkBitmap chunk2 = ChunkBitmap.Create(rowCount);
        chunk2.SetBit(3);

        DataValue value = DataValue.FromString("rare");

        Dictionary<DataValue, byte[][]> compressedBitmaps = new()
        {
            [value] = [chunk0.Compress(), Array.Empty<byte>(), chunk2.Compress()],
        };

        BitmapColumnIndex original = new(compressedBitmaps, chunkCount, chunkRowCounts);
        SourceIndex restored = WriteAndRead(BuildSourceIndex("tag", original));

        Assert.NotNull(restored.BitmapIndexes);
        Assert.True(restored.BitmapIndexes.TryGetIndex("tag", out BitmapColumnIndex? restoredIndex));

        IReadOnlySet<int> chunkSet = restoredIndex.FindChunksContaining(value);
        Assert.Equal(2, chunkSet.Count);
        Assert.Contains(0, chunkSet);
        Assert.Contains(2, chunkSet);
        Assert.DoesNotContain(1, chunkSet);
    }

    // ───────────────────────── ChunkContainsValue after round-trip ─────────────────────────

    [Fact]
    public void RoundTrip_ChunkContainsValue_WorksAfterDeserialization()
    {
        int rowCount = 8;
        int chunkCount = 2;
        int[] chunkRowCounts = [rowCount, rowCount];

        ChunkBitmap chunk0 = ChunkBitmap.Create(rowCount);
        chunk0.SetBit(1);

        DataValue value = DataValue.FromFloat32(42.0f);

        Dictionary<DataValue, byte[][]> compressedBitmaps = new()
        {
            [value] = [chunk0.Compress(), Array.Empty<byte>()],
        };

        BitmapColumnIndex original = new(compressedBitmaps, chunkCount, chunkRowCounts);
        SourceIndex restored = WriteAndRead(BuildSourceIndex("score", original));

        Assert.NotNull(restored.BitmapIndexes);
        Assert.True(restored.BitmapIndexes.TryGetIndex("score", out BitmapColumnIndex? restoredIndex));

        Assert.True(restoredIndex.ChunkContainsValue(value, 0));
        Assert.False(restoredIndex.ChunkContainsValue(value, 1));
    }

    // ───────────────────────── Empty bitmap column ─────────────────────────

    [Fact]
    public void RoundTrip_EmptyBitmapPayloads_HandledCorrectly()
    {
        int rowCount = 8;
        int chunkCount = 1;
        int[] chunkRowCounts = [rowCount];

        // Value with all-empty compressed payload (value never appears).
        DataValue value = DataValue.FromString("phantom");

        Dictionary<DataValue, byte[][]> compressedBitmaps = new()
        {
            [value] = [Array.Empty<byte>()],
        };

        BitmapColumnIndex original = new(compressedBitmaps, chunkCount, chunkRowCounts);
        SourceIndex restored = WriteAndRead(BuildSourceIndex("ghost", original));

        Assert.NotNull(restored.BitmapIndexes);
        Assert.True(restored.BitmapIndexes.TryGetIndex("ghost", out BitmapColumnIndex? restoredIndex));

        Assert.False(restoredIndex.ChunkContainsValue(value, 0));
        ChunkBitmap bitmap = restoredIndex.GetChunkBitmap(value, 0);
        Assert.True(bitmap.IsEmpty);
    }

    // ───────────────────────── Coexists with other section types ─────────────────────────

    [Fact]
    public void RoundTrip_BitmapWithBloomFilters_BothPreserved()
    {
        int rowCount = 8;

        BitmapColumnIndex bitmapIndex = BuildSingleValueIndex(
            DataValue.FromBoolean(true), rowCount, chunkCount: 1, setRows: [0, 7]);

        Dictionary<string, BitmapColumnIndex> bitmapIndexes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["flag"] = bitmapIndex,
        };

        BloomFilter bloomFilter = new(new byte[16], 128, 3);
        Dictionary<string, BloomFilter[]> bloomFilters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["flag"] = [bloomFilter],
        };

        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([new ColumnInfo("flag", DataKind.Boolean, nullable: false)]);
        IndexSchema indexSchema = new(schema, rowCount);

        SourceIndex sourceIndex = new(
            fingerprint, indexSchema, Array.Empty<IndexChunk>(),
            bloomFilters: new BloomFilterSet(bloomFilters, 1),
            sortedIndexes: null,
            zipDirectory: null,
            bPlusTreeIndexes: null,
            bitmapIndexes: new BitmapIndexSet(bitmapIndexes));

        SourceIndex restored = WriteAndRead(sourceIndex);

        Assert.NotNull(restored.BloomFilters);
        Assert.NotNull(restored.BitmapIndexes);
        Assert.True(restored.BitmapIndexes.TryGetIndex("flag", out _));
    }

    // ───────────────────────── DataValue kinds ─────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RoundTrip_BooleanValues_Preserved(bool boolValue)
    {
        DataValue value = DataValue.FromBoolean(boolValue);
        BitmapColumnIndex original = BuildSingleValueIndex(value, rowCount: 4, chunkCount: 1, setRows: [1]);

        SourceIndex restored = WriteAndRead(BuildSourceIndex("flag", original));

        Assert.NotNull(restored.BitmapIndexes);
        Assert.True(restored.BitmapIndexes.TryGetIndex("flag", out BitmapColumnIndex? restoredIndex));

        ChunkBitmap bitmap = restoredIndex.GetChunkBitmap(value, 0);
        Assert.Equal(1, bitmap.PopCount());
        Assert.True(bitmap.IsSet(1));
    }

    [Fact]
    public void RoundTrip_ScalarValues_Preserved()
    {
        DataValue value = DataValue.FromFloat32(3.14f);
        BitmapColumnIndex original = BuildSingleValueIndex(value, rowCount: 8, chunkCount: 1, setRows: [0, 7]);

        SourceIndex restored = WriteAndRead(BuildSourceIndex("score", original));

        Assert.NotNull(restored.BitmapIndexes);
        Assert.True(restored.BitmapIndexes.TryGetIndex("score", out BitmapColumnIndex? restoredIndex));

        Assert.Contains(value, restoredIndex.DistinctValues);
    }

    [Fact]
    public void RoundTrip_UInt8Values_Preserved()
    {
        DataValue value = DataValue.FromUInt8(42);
        BitmapColumnIndex original = BuildSingleValueIndex(value, rowCount: 8, chunkCount: 1, setRows: [2]);

        SourceIndex restored = WriteAndRead(BuildSourceIndex("level", original));

        Assert.NotNull(restored.BitmapIndexes);
        Assert.True(restored.BitmapIndexes.TryGetIndex("level", out BitmapColumnIndex? restoredIndex));

        Assert.Contains(value, restoredIndex.DistinctValues);
        ChunkBitmap bitmap = restoredIndex.GetChunkBitmap(value, 0);
        Assert.True(bitmap.IsSet(2));
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static BitmapColumnIndex BuildSingleValueIndex(
        DataValue value, int rowCount, int chunkCount, int[] setRows)
    {
        int[] chunkRowCounts = new int[chunkCount];
        Array.Fill(chunkRowCounts, rowCount);

        byte[][] chunks = new byte[chunkCount][];

        for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            ChunkBitmap bitmap = ChunkBitmap.Create(rowCount);

            if (chunkIndex == 0)
            {
                foreach (int row in setRows)
                {
                    bitmap.SetBit(row);
                }
            }

            byte[] compressed = bitmap.Compress();
            chunks[chunkIndex] = chunkIndex == 0 ? compressed : Array.Empty<byte>();
        }

        Dictionary<DataValue, byte[][]> compressedBitmaps = new()
        {
            [value] = chunks,
        };

        return new BitmapColumnIndex(compressedBitmaps, chunkCount, chunkRowCounts);
    }

    private static BitmapColumnIndex BuildMultiValueIndex(int rowCount, int chunkCount)
    {
        int[] chunkRowCounts = new int[chunkCount];
        Array.Fill(chunkRowCounts, rowCount);

        ChunkBitmap redBitmap = ChunkBitmap.Create(rowCount);
        redBitmap.SetBit(0);
        redBitmap.SetBit(3);

        ChunkBitmap blueBitmap = ChunkBitmap.Create(rowCount);
        blueBitmap.SetBit(1);
        blueBitmap.SetBit(4);

        ChunkBitmap greenBitmap = ChunkBitmap.Create(rowCount);
        greenBitmap.SetBit(7);

        byte[][] redChunks = new byte[chunkCount][];
        byte[][] blueChunks = new byte[chunkCount][];
        byte[][] greenChunks = new byte[chunkCount][];

        redChunks[0] = redBitmap.Compress();
        blueChunks[0] = blueBitmap.Compress();
        greenChunks[0] = greenBitmap.Compress();

        for (int i = 1; i < chunkCount; i++)
        {
            redChunks[i] = Array.Empty<byte>();
            blueChunks[i] = Array.Empty<byte>();
            greenChunks[i] = Array.Empty<byte>();
        }

        Dictionary<DataValue, byte[][]> compressedBitmaps = new()
        {
            [DataValue.FromString("red")] = redChunks,
            [DataValue.FromString("blue")] = blueChunks,
            [DataValue.FromString("green")] = greenChunks,
        };

        return new BitmapColumnIndex(compressedBitmaps, chunkCount, chunkRowCounts);
    }

    private static SourceIndex BuildSourceIndex(string columnName, BitmapColumnIndex bitmapIndex)
    {
        Dictionary<string, BitmapColumnIndex> indexes = new(StringComparer.OrdinalIgnoreCase)
        {
            [columnName] = bitmapIndex,
        };

        BitmapIndexSet bitmapSet = new(indexes);
        return BuildSourceIndexWithBitmapSet(bitmapSet);
    }

    private static SourceIndex BuildSourceIndexWithBitmapSet(BitmapIndexSet bitmapSet)
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([new ColumnInfo("value", DataKind.String, nullable: false)]);
        IndexSchema indexSchema = new(schema, 100);

        return new SourceIndex(fingerprint, indexSchema, Array.Empty<IndexChunk>(),
            bloomFilters: null, sortedIndexes: null, zipDirectory: null,
            bPlusTreeIndexes: null, bitmapIndexes: bitmapSet);
    }

    private static SourceIndex WriteAndRead(SourceIndex index)
    {
        using MemoryStream stream = new();
        IndexWriter writer = new();
        SourceIndexSet indexSet = SourceIndexSet.Create("test", index);
        writer.Write(indexSet, stream);

        stream.Position = 0;
        IndexReader reader = new();
        SourceIndexSet restoredSet = reader.Read(stream);
        return restoredSet.Tables["test"];
    }
}
