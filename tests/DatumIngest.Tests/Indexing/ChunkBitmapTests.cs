using DatumIngest.Indexing.Bitmap;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// Tests for <see cref="ChunkBitmap"/> — bitset creation, bit manipulation,
/// compression round-trips, population count, and edge cases.
/// </summary>
public sealed class ChunkBitmapTests
{
    // ───────────────────────── Creation ─────────────────────────

    [Fact]
    public void Create_AllBitsZero()
    {
        ChunkBitmap bitmap = ChunkBitmap.Create(100);

        Assert.Equal(100, bitmap.RowCount);
        Assert.Equal(13, bitmap.ByteLength); // ceil(100 / 8)
        Assert.True(bitmap.IsEmpty);
        Assert.Equal(0, bitmap.PopCount());
    }

    [Fact]
    public void Create_ExactMultipleOfEight()
    {
        ChunkBitmap bitmap = ChunkBitmap.Create(64);

        Assert.Equal(64, bitmap.RowCount);
        Assert.Equal(8, bitmap.ByteLength);
    }

    [Fact]
    public void Create_SingleRow()
    {
        ChunkBitmap bitmap = ChunkBitmap.Create(1);

        Assert.Equal(1, bitmap.RowCount);
        Assert.Equal(1, bitmap.ByteLength);
    }

    // ───────────────────────── SetBit / IsSet ─────────────────────────

    [Fact]
    public void SetBit_MakesBitReadable()
    {
        ChunkBitmap bitmap = ChunkBitmap.Create(16);

        bitmap.SetBit(0);
        bitmap.SetBit(7);
        bitmap.SetBit(8);
        bitmap.SetBit(15);

        Assert.True(bitmap.IsSet(0));
        Assert.True(bitmap.IsSet(7));
        Assert.True(bitmap.IsSet(8));
        Assert.True(bitmap.IsSet(15));

        Assert.False(bitmap.IsSet(1));
        Assert.False(bitmap.IsSet(6));
        Assert.False(bitmap.IsSet(9));
        Assert.False(bitmap.IsSet(14));
    }

    [Fact]
    public void SetBit_Idempotent()
    {
        ChunkBitmap bitmap = ChunkBitmap.Create(8);

        bitmap.SetBit(3);
        bitmap.SetBit(3);

        Assert.Equal(1, bitmap.PopCount());
    }

    [Fact]
    public void ClearBit_RemovesBit()
    {
        ChunkBitmap bitmap = ChunkBitmap.Create(8);

        bitmap.SetBit(3);
        Assert.True(bitmap.IsSet(3));

        bitmap.ClearBit(3);
        Assert.False(bitmap.IsSet(3));
        Assert.True(bitmap.IsEmpty);
    }

    // ───────────────────────── IsEmpty ─────────────────────────

    [Fact]
    public void IsEmpty_FalseWhenBitSet()
    {
        ChunkBitmap bitmap = ChunkBitmap.Create(100);

        bitmap.SetBit(99);

        Assert.False(bitmap.IsEmpty);
    }

    // ───────────────────────── PopCount ─────────────────────────

    [Fact]
    public void PopCount_MatchesSetBits()
    {
        ChunkBitmap bitmap = ChunkBitmap.Create(100);

        bitmap.SetBit(0);
        bitmap.SetBit(10);
        bitmap.SetBit(50);
        bitmap.SetBit(99);

        Assert.Equal(4, bitmap.PopCount());
    }

    [Fact]
    public void PopCount_AllBitsSet()
    {
        ChunkBitmap bitmap = ChunkBitmap.Create(8);

        for (int i = 0; i < 8; i++)
        {
            bitmap.SetBit(i);
        }

        Assert.Equal(8, bitmap.PopCount());
    }

    // ───────────────────────── EnumerateSetBits ─────────────────────────

    [Fact]
    public void EnumerateSetBits_ReturnsCorrectPositions()
    {
        ChunkBitmap bitmap = ChunkBitmap.Create(20);

        bitmap.SetBit(3);
        bitmap.SetBit(7);
        bitmap.SetBit(11);
        bitmap.SetBit(19);

        List<int> setBits = bitmap.EnumerateSetBits().ToList();

        Assert.Equal([3, 7, 11, 19], setBits);
    }

    [Fact]
    public void EnumerateSetBits_EmptyBitmap_ReturnsNone()
    {
        ChunkBitmap bitmap = ChunkBitmap.Create(100);

        Assert.Empty(bitmap.EnumerateSetBits());
    }

    [Fact]
    public void EnumerateSetBits_RespectsRowCount()
    {
        // 10 rows = 2 bytes, but only 10 bits valid.
        ChunkBitmap bitmap = ChunkBitmap.Create(10);

        // Set all 16 bits in the 2 bytes.
        for (int i = 0; i < 10; i++)
        {
            bitmap.SetBit(i);
        }

        List<int> setBits = bitmap.EnumerateSetBits().ToList();

        Assert.Equal(10, setBits.Count);
        Assert.Equal(9, setBits[^1]);
    }

    // ───────────────────────── Compression round-trip ─────────────────────────

    [Fact]
    public void CompressDecompress_RoundTrips()
    {
        ChunkBitmap original = ChunkBitmap.Create(1000);

        // Set every third row.
        for (int i = 0; i < 1000; i += 3)
        {
            original.SetBit(i);
        }

        byte[] compressed = original.Compress();
        ChunkBitmap restored = ChunkBitmap.FromCompressed(compressed, 1000);

        Assert.Equal(original.RowCount, restored.RowCount);
        Assert.Equal(original.ByteLength, restored.ByteLength);
        Assert.Equal(original.PopCount(), restored.PopCount());

        for (int i = 0; i < 1000; i++)
        {
            Assert.Equal(original.IsSet(i), restored.IsSet(i));
        }
    }

    [Fact]
    public void CompressDecompress_EmptyBitmap_RoundTrips()
    {
        ChunkBitmap original = ChunkBitmap.Create(500);

        byte[] compressed = original.Compress();
        ChunkBitmap restored = ChunkBitmap.FromCompressed(compressed, 500);

        Assert.True(restored.IsEmpty);
    }

    [Fact]
    public void CompressDecompress_AllBitsSet_RoundTrips()
    {
        ChunkBitmap original = ChunkBitmap.Create(64);

        for (int i = 0; i < 64; i++)
        {
            original.SetBit(i);
        }

        byte[] compressed = original.Compress();
        ChunkBitmap restored = ChunkBitmap.FromCompressed(compressed, 64);

        Assert.Equal(64, restored.PopCount());
    }

    // ───────────────────────── Large bitmap ─────────────────────────

    [Fact]
    public void LargeBitmap_10000Rows_WorksCorrectly()
    {
        ChunkBitmap bitmap = ChunkBitmap.Create(10_000);

        Assert.Equal(1250, bitmap.ByteLength); // 10000 / 8

        bitmap.SetBit(0);
        bitmap.SetBit(9999);

        Assert.True(bitmap.IsSet(0));
        Assert.True(bitmap.IsSet(9999));
        Assert.False(bitmap.IsSet(5000));

        byte[] compressed = bitmap.Compress();
        ChunkBitmap restored = ChunkBitmap.FromCompressed(compressed, 10_000);

        Assert.Equal(2, restored.PopCount());
        Assert.True(restored.IsSet(0));
        Assert.True(restored.IsSet(9999));
    }
}
