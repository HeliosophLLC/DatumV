using DatumIngest.Serialization.Fits;

namespace DatumIngest.Tests.Serialization.Fits;

/// <summary>
/// Unit tests for <see cref="FitsHduDescriptor"/> covering header parsing,
/// derived metadata extraction (BITPIX / NAXIS / EXTNAME / BSCALE / BZERO /
/// TFIELDS), the 2880-byte block layout, and multi-HDU stream walks.
/// </summary>
public sealed class FitsHduDescriptorTests
{
    // ───────────────────── Primary HDU shapes ─────────────────────

    [Fact]
    public void Read_PrimaryHeaderOnly_ParsesBasicCards()
    {
        // A NAXIS=0 primary HDU — the "container" form used when the
        // scientific payload is in an extension. SDSS data products commonly
        // ship in this shape.
        byte[] file = new FitsTestBuilder()
            .BeginPrimary()
            .Int("BITPIX", 8)
            .Int("NAXIS", 0)
            .Bool("EXTEND", true)
            .EndHdu()
            .Build();

        using MemoryStream stream = new(file);
        FitsHduDescriptor hdu = FitsHduDescriptor.Read(stream, isPrimary: true);

        Assert.Equal(FitsHduKind.Primary, hdu.Kind);
        Assert.Equal(8, hdu.BitPix);
        Assert.Equal(0, hdu.NAxis);
        Assert.Empty(hdu.NAxisN);
        Assert.Equal(0, hdu.DataByteSize);
        Assert.False(hdu.HasData);
    }

    [Fact]
    public void Read_PrimaryImage_4x4_Int16_ComputesDataByteSize()
    {
        // 4×4 16-bit integer image: 4 × 4 × 2 = 32 bytes of pixel data,
        // padded out to 2880.
        byte[] file = new FitsTestBuilder()
            .BeginPrimary()
            .Int("BITPIX", 16)
            .Int("NAXIS", 2)
            .Int("NAXIS1", 4)
            .Int("NAXIS2", 4)
            .EndHdu()
            .AppendData(new byte[32])
            .Build();

        using MemoryStream stream = new(file);
        FitsHduDescriptor hdu = FitsHduDescriptor.Read(stream, isPrimary: true);

        Assert.Equal(16, hdu.BitPix);
        Assert.Equal(2, hdu.NAxis);
        Assert.Equal(new[] { 4, 4 }, hdu.NAxisN);
        Assert.Equal(32, hdu.DataByteSize);
        Assert.Equal(2880, hdu.DataPaddedByteSize);
        Assert.True(hdu.HasData);
        Assert.Equal(16, hdu.PixelCount);
        Assert.Equal(2, hdu.BytesPerPixel);
    }

    [Fact]
    public void Read_PrimaryWithBScaleBZero_ExposesScaling()
    {
        byte[] file = new FitsTestBuilder()
            .BeginPrimary()
            .Int("BITPIX", 16)
            .Int("NAXIS", 0)
            .Double("BSCALE", 2.5)
            .Double("BZERO", -1024.0)
            .EndHdu()
            .Build();

        using MemoryStream stream = new(file);
        FitsHduDescriptor hdu = FitsHduDescriptor.Read(stream, isPrimary: true);

        Assert.Equal(2.5, hdu.BScale);
        Assert.Equal(-1024.0, hdu.BZero);
    }

    [Fact]
    public void Read_BScaleBZeroAbsent_DefaultsToOneAndZero()
    {
        byte[] file = new FitsTestBuilder()
            .BeginPrimary()
            .Int("BITPIX", 16)
            .Int("NAXIS", 0)
            .EndHdu()
            .Build();

        using MemoryStream stream = new(file);
        FitsHduDescriptor hdu = FitsHduDescriptor.Read(stream, isPrimary: true);

        Assert.Equal(1.0, hdu.BScale);
        Assert.Equal(0.0, hdu.BZero);
    }

    // ───────────────────── Extension HDU classification ─────────────────────

    [Fact]
    public void Read_ImageExtension_ClassifiedAsImage()
    {
        byte[] header = new FitsTestBuilder()
            .BeginExtension("IMAGE")
            .Int("BITPIX", -32)
            .Int("NAXIS", 2)
            .Int("NAXIS1", 8)
            .Int("NAXIS2", 8)
            .Int("PCOUNT", 0)
            .Int("GCOUNT", 1)
            .QuotedString("EXTNAME", "SCI")
            .Int("EXTVER", 1)
            .EndHdu()
            .AppendData(new byte[8 * 8 * 4])
            .Build();

        using MemoryStream stream = new(header);
        FitsHduDescriptor hdu = FitsHduDescriptor.Read(stream, isPrimary: false);

        Assert.Equal(FitsHduKind.Image, hdu.Kind);
        Assert.Equal(-32, hdu.BitPix);
        Assert.Equal("SCI", hdu.ExtName);
        Assert.Equal(1, hdu.ExtVer);
        Assert.Equal(8 * 8 * 4, hdu.DataByteSize);
    }

    [Fact]
    public void Read_BinTableExtension_ClassifiedAsBinTableWithTFields()
    {
        // BINTABLE: NAXIS1 = row bytes, NAXIS2 = row count, TFIELDS = column count.
        byte[] file = new FitsTestBuilder()
            .BeginExtension("BINTABLE")
            .Int("BITPIX", 8)
            .Int("NAXIS", 2)
            .Int("NAXIS1", 16)
            .Int("NAXIS2", 100)
            .Int("PCOUNT", 0)
            .Int("GCOUNT", 1)
            .Int("TFIELDS", 3)
            .EndHdu()
            .AppendData(new byte[16 * 100])
            .Build();

        using MemoryStream stream = new(file);
        FitsHduDescriptor hdu = FitsHduDescriptor.Read(stream, isPrimary: false);

        Assert.Equal(FitsHduKind.BinTable, hdu.Kind);
        Assert.Equal(3, hdu.TFields);
        Assert.Equal(16 * 100, hdu.DataByteSize);
    }

    [Fact]
    public void Read_UnknownXtension_ClassifiedAsUnknown()
    {
        byte[] file = new FitsTestBuilder()
            .BeginExtension("FOOBAR")
            .Int("BITPIX", 8)
            .Int("NAXIS", 0)
            .Int("PCOUNT", 0)
            .Int("GCOUNT", 1)
            .EndHdu()
            .Build();

        using MemoryStream stream = new(file);
        FitsHduDescriptor hdu = FitsHduDescriptor.Read(stream, isPrimary: false);

        Assert.Equal(FitsHduKind.Unknown, hdu.Kind);
    }

    // ───────────────────── Stream walk ─────────────────────

    [Fact]
    public void SkipData_AdvancesPastPaddedData_NextReadHitsSecondHdu()
    {
        byte[] file = new FitsTestBuilder()
            .BeginPrimary()
            .Int("BITPIX", 16)
            .Int("NAXIS", 2)
            .Int("NAXIS1", 4)
            .Int("NAXIS2", 4)
            .Int("EXTEND", 1)
            .EndHdu()
            .AppendData(new byte[32])
            .BeginExtension("IMAGE")
            .Int("BITPIX", 8)
            .Int("NAXIS", 0)
            .Int("PCOUNT", 0)
            .Int("GCOUNT", 1)
            .QuotedString("EXTNAME", "META")
            .EndHdu()
            .Build();

        using MemoryStream stream = new(file);
        FitsHduDescriptor primary = FitsHduDescriptor.Read(stream, isPrimary: true);
        primary.SkipData(stream);

        Assert.True(FitsHduDescriptor.TryReadNext(stream, isPrimary: false, out FitsHduDescriptor? ext));
        Assert.Equal(FitsHduKind.Image, ext!.Kind);
        Assert.Equal("META", ext.ExtName);
    }

    [Fact]
    public void TryReadNext_AtCleanEof_ReturnsFalse()
    {
        byte[] file = new FitsTestBuilder()
            .BeginPrimary()
            .Int("BITPIX", 8)
            .Int("NAXIS", 0)
            .EndHdu()
            .Build();

        using MemoryStream stream = new(file);
        FitsHduDescriptor primary = FitsHduDescriptor.Read(stream, isPrimary: true);
        primary.SkipData(stream);

        Assert.False(FitsHduDescriptor.TryReadNext(stream, isPrimary: false, out FitsHduDescriptor? next));
        Assert.Null(next);
    }

    // ───────────────────── Multi-block headers ─────────────────────

    [Fact]
    public void Read_HeaderSpanningTwoBlocks_ParsesAllCards()
    {
        // 36 cards fit in one block. Force a second block by adding 40
        // COMMENT cards on top of the mandatory keywords.
        FitsTestBuilder builder = new FitsTestBuilder()
            .BeginPrimary()
            .Int("BITPIX", 8)
            .Int("NAXIS", 0);
        for (int i = 0; i < 40; i++)
        {
            builder.Comment($"line {i}");
        }
        builder.EndHdu();
        byte[] file = builder.Build();

        // Two 2880-byte blocks = 5760 bytes of header.
        Assert.True(file.Length >= 5760);

        using MemoryStream stream = new(file);
        FitsHduDescriptor hdu = FitsHduDescriptor.Read(stream, isPrimary: true);

        int commentCount = 0;
        foreach (FitsCard card in hdu.Cards)
        {
            if (card.Keyword == "COMMENT") commentCount++;
        }
        Assert.Equal(40, commentCount);
        Assert.Equal(5760, stream.Position);
    }

    // ───────────────────── Block padding math ─────────────────────

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2880)]
    [InlineData(2880, 2880)]
    [InlineData(2881, 5760)]
    [InlineData(5760, 5760)]
    public void RoundUpToBlock_PadsToNext2880Boundary(long size, long expected)
    {
        Assert.Equal(expected, FitsHduDescriptor.RoundUpToBlock(size));
    }

    // ───────────────────── Malformed inputs ─────────────────────

    [Fact]
    public void Read_PrimaryWithoutSimpleCard_Throws()
    {
        byte[] file = new FitsTestBuilder()
            .BeginExtension("IMAGE") // wrong first card for primary
            .Int("BITPIX", 8)
            .Int("NAXIS", 0)
            .EndHdu()
            .Build();

        using MemoryStream stream = new(file);
        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => FitsHduDescriptor.Read(stream, isPrimary: true));
        Assert.Contains("SIMPLE", ex.Message);
    }

    [Fact]
    public void Read_ExtensionWithoutXtensionCard_Throws()
    {
        byte[] file = new FitsTestBuilder()
            .BeginPrimary()
            .Int("BITPIX", 8)
            .Int("NAXIS", 0)
            .EndHdu()
            .Build();

        using MemoryStream stream = new(file);
        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => FitsHduDescriptor.Read(stream, isPrimary: false));
        Assert.Contains("XTENSION", ex.Message);
    }

    [Fact]
    public void Read_MissingDimensionCard_Throws()
    {
        // NAXIS=2 but NAXIS2 is missing.
        FitsTestBuilder builder = new FitsTestBuilder()
            .BeginPrimary()
            .Int("BITPIX", 16)
            .Int("NAXIS", 2)
            .Int("NAXIS1", 4)
            .EndHdu();
        byte[] file = builder.Build();

        using MemoryStream stream = new(file);
        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => FitsHduDescriptor.Read(stream, isPrimary: true));
        Assert.Contains("NAXIS2", ex.Message);
    }
}
