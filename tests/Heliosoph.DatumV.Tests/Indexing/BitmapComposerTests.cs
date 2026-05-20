using Heliosoph.DatumV.Indexing.Bitmap;

namespace Heliosoph.DatumV.Tests.Indexing;

/// <summary>
/// Tests for <see cref="BitmapComposer"/> — SIMD-accelerated bitwise AND, OR, NOT,
/// PopCount, and EnumerateSetBits operations on byte spans.
/// </summary>
public sealed class BitmapComposerTests : ServiceTestBase
{
    // ───────────────────────── AND ─────────────────────────

    [Fact]
    public void And_Intersection()
    {
        byte[] left = [0b11110000, 0b10101010];
        byte[] right = [0b11001100, 0b11001100];

        byte[] result = BitmapComposer.And(left, right);

        Assert.Equal([0b11000000, 0b10001000], result);
    }

    [Fact]
    public void And_AllZeros()
    {
        byte[] left = [0xFF, 0xFF];
        byte[] right = [0x00, 0x00];

        byte[] result = BitmapComposer.And(left, right);

        Assert.Equal([0x00, 0x00], result);
    }

    [Fact]
    public void And_AllOnes()
    {
        byte[] left = [0xFF, 0xFF];
        byte[] right = [0xFF, 0xFF];

        byte[] result = BitmapComposer.And(left, right);

        Assert.Equal([0xFF, 0xFF], result);
    }

    [Fact]
    public void And_LargeSpan_UsesSimdPath()
    {
        // 128 bytes = well above a SIMD vector width to exercise the SIMD loop.
        byte[] left = new byte[128];
        byte[] right = new byte[128];
        Array.Fill(left, (byte)0xFF);
        Array.Fill(right, (byte)0xAA);

        byte[] result = BitmapComposer.And(left, right);

        for (int i = 0; i < 128; i++)
        {
            Assert.Equal(0xAA, result[i]);
        }
    }

    // ───────────────────────── OR ─────────────────────────

    [Fact]
    public void Or_Union()
    {
        byte[] left = [0b11110000, 0b10101010];
        byte[] right = [0b00001111, 0b01010101];

        byte[] result = BitmapComposer.Or(left, right);

        Assert.Equal([0xFF, 0xFF], result);
    }

    [Fact]
    public void Or_LargeSpan_UsesSimdPath()
    {
        byte[] left = new byte[128];
        byte[] right = new byte[128];
        Array.Fill(left, (byte)0xF0);
        Array.Fill(right, (byte)0x0F);

        byte[] result = BitmapComposer.Or(left, right);

        for (int i = 0; i < 128; i++)
        {
            Assert.Equal(0xFF, result[i]);
        }
    }

    // ───────────────────────── NOT ─────────────────────────

    [Fact]
    public void Not_Inverts()
    {
        byte[] source = [0b11110000, 0b00001111];

        byte[] result = BitmapComposer.Not(source, 16);

        Assert.Equal([0b00001111, 0b11110000], result);
    }

    [Fact]
    public void Not_MasksTrailingBits()
    {
        // 10 rows = 2 bytes, but only 10 bits valid (bits 10-15 should be zero).
        byte[] source = [0x00, 0x00];

        byte[] result = BitmapComposer.Not(source, 10);

        Assert.Equal(0xFF, result[0]); // Bits 0-7: all set.
        Assert.Equal(0b00000011, result[1]); // Bits 8-9: set. Bits 10-15: masked off.
    }

    [Fact]
    public void Not_ExactMultipleOfEight_NoMasking()
    {
        byte[] source = [0x00, 0x00];

        byte[] result = BitmapComposer.Not(source, 16);

        Assert.Equal(0xFF, result[0]);
        Assert.Equal(0xFF, result[1]);
    }

    [Fact]
    public void Not_LargeSpan_UsesSimdPath()
    {
        byte[] source = new byte[128];
        Array.Fill(source, (byte)0xAA);

        byte[] result = BitmapComposer.Not(source, 128 * 8);

        for (int i = 0; i < 128; i++)
        {
            Assert.Equal(0x55, result[i]);
        }
    }

    // ───────────────────────── PopCount ─────────────────────────

    [Fact]
    public void PopCount_Empty()
    {
        byte[] bits = [0x00, 0x00];

        Assert.Equal(0, BitmapComposer.PopCount(bits));
    }

    [Fact]
    public void PopCount_AllOnes()
    {
        byte[] bits = [0xFF, 0xFF];

        Assert.Equal(16, BitmapComposer.PopCount(bits));
    }

    [Fact]
    public void PopCount_Mixed()
    {
        byte[] bits = [0b10101010, 0b01010101, 0b11000011];

        Assert.Equal(12, BitmapComposer.PopCount(bits));
    }

    [Fact]
    public void PopCount_LargeSpan()
    {
        byte[] bits = new byte[128];
        Array.Fill(bits, (byte)0xFF);

        Assert.Equal(1024, BitmapComposer.PopCount(bits));
    }

    // ───────────────────────── EnumerateSetBits ─────────────────────────

    [Fact]
    public void EnumerateSetBits_ReturnsCorrectPositions()
    {
        byte[] bits = [0b00001001]; // bits 0 and 3

        List<int> result = BitmapComposer.EnumerateSetBits(bits, 8).ToList();

        Assert.Equal([0, 3], result);
    }

    [Fact]
    public void EnumerateSetBits_RespectsMaxBit()
    {
        byte[] bits = [0xFF]; // all 8 bits set

        List<int> result = BitmapComposer.EnumerateSetBits(bits, 4).ToList();

        Assert.Equal([0, 1, 2, 3], result);
    }

    [Fact]
    public void EnumerateSetBits_EmptyBits()
    {
        byte[] bits = [0x00, 0x00];

        Assert.Empty(BitmapComposer.EnumerateSetBits(bits, 16));
    }

    // ───────────────────────── AnySet ─────────────────────────

    [Fact]
    public void AnySet_AllZeros_ReturnsFalse()
    {
        byte[] bits = new byte[64];

        Assert.False(BitmapComposer.AnySet(bits));
    }

    [Fact]
    public void AnySet_OneBitSet_ReturnsTrue()
    {
        byte[] bits = new byte[64];
        bits[63] = 0x01;

        Assert.True(BitmapComposer.AnySet(bits));
    }

    [Fact]
    public void AnySet_LargeAllOnes_ReturnsTrue()
    {
        byte[] bits = new byte[128];
        Array.Fill(bits, (byte)0xFF);

        Assert.True(BitmapComposer.AnySet(bits));
    }

    // ───────────────────────── Composition round-trip ─────────────────────────

    [Fact]
    public void Composition_AndOrNot_ProducesExpectedResult()
    {
        // Simulate: WHERE color = 'red' AND size != 'large'
        // color='red':   rows 0,1,2,3,4  → 0b00011111
        // size='large':  rows 2,3        → 0b00001100
        // NOT large:                     → 0b11110011 (masked to 8 rows)
        // AND:                           → 0b00010011 (rows 0,1,4)
        byte[] colorRed = [0b00011111];
        byte[] sizeLarge = [0b00001100];

        byte[] notLarge = BitmapComposer.Not(sizeLarge, 8);
        byte[] result = BitmapComposer.And(colorRed, notLarge);

        List<int> matchingRows = BitmapComposer.EnumerateSetBits(result, 8).ToList();

        Assert.Equal([0, 1, 4], matchingRows);
    }
}
