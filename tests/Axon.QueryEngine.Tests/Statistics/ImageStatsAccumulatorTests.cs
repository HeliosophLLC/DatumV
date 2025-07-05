namespace Axon.QueryEngine.Tests.Statistics;

using Axon.QueryEngine.Functions.Image;
using Axon.QueryEngine.Model;
using Axon.QueryEngine.Statistics;
using Axon.QueryEngine.Statistics.Accumulators;

public sealed class ImageStatsAccumulatorTests
{
    // Minimal valid JPEG: SOI + SOF0 marker with 3 channels, 480×640
    private static byte[] MakeJpegHeader(int width, int height, int channels)
    {
        // FF D8 (SOI) + FF E0 (APP0 marker) with short segment + FF C0 (SOF0) with dimensions
        byte[] data = new byte[20];
        data[0] = 0xFF;
        data[1] = 0xD8; // SOI

        // SOF0 marker
        data[2] = 0xFF;
        data[3] = 0xC0; // SOF0
        data[4] = 0x00;
        data[5] = 0x0B; // length = 11 (includes 2 length bytes)
        data[6] = 0x08; // precision = 8 bits
        data[7] = (byte)(height >> 8);
        data[8] = (byte)(height & 0xFF);
        data[9] = (byte)(width >> 8);
        data[10] = (byte)(width & 0xFF);
        data[11] = (byte)channels;

        return data;
    }

    // Minimal valid PNG with IHDR
    private static byte[] MakePngHeader(int width, int height, byte colorType)
    {
        byte[] data = new byte[30];
        // PNG signature
        data[0] = 0x89;
        data[1] = 0x50;
        data[2] = 0x4E;
        data[3] = 0x47;
        data[4] = 0x0D;
        data[5] = 0x0A;
        data[6] = 0x1A;
        data[7] = 0x0A;

        // IHDR chunk: length
        data[8] = 0x00;
        data[9] = 0x00;
        data[10] = 0x00;
        data[11] = 0x0D; // 13 bytes

        // IHDR type
        data[12] = (byte)'I';
        data[13] = (byte)'H';
        data[14] = (byte)'D';
        data[15] = (byte)'R';

        // Width (big-endian)
        data[16] = (byte)(width >> 24);
        data[17] = (byte)(width >> 16);
        data[18] = (byte)(width >> 8);
        data[19] = (byte)(width & 0xFF);

        // Height (big-endian)
        data[20] = (byte)(height >> 24);
        data[21] = (byte)(height >> 16);
        data[22] = (byte)(height >> 8);
        data[23] = (byte)(height & 0xFF);

        // Bit depth
        data[24] = 8;

        // Color type
        data[25] = colorType;

        return data;
    }

    [Fact]
    public void TryParseHeader_JpegSof0_ExtractsDimensions()
    {
        byte[] jpeg = MakeJpegHeader(640, 480, 3);

        ImageDimensions? dims = ImageHeaderParser.TryParseHeader(jpeg);

        Assert.NotNull(dims);
        Assert.Equal(640, dims.Width);
        Assert.Equal(480, dims.Height);
        Assert.Equal(3, dims.Channels);
    }

    [Fact]
    public void TryParseHeader_JpegSingleChannel_ExtractsCorrectly()
    {
        byte[] jpeg = MakeJpegHeader(320, 240, 1);

        ImageDimensions? dims = ImageHeaderParser.TryParseHeader(jpeg);

        Assert.NotNull(dims);
        Assert.Equal(320, dims.Width);
        Assert.Equal(240, dims.Height);
        Assert.Equal(1, dims.Channels);
    }

    [Fact]
    public void TryParseHeader_PngRgb_ExtractsDimensions()
    {
        byte[] png = MakePngHeader(1920, 1080, 2); // color type 2 = RGB

        ImageDimensions? dims = ImageHeaderParser.TryParseHeader(png);

        Assert.NotNull(dims);
        Assert.Equal(1920, dims.Width);
        Assert.Equal(1080, dims.Height);
        Assert.Equal(3, dims.Channels); // RGB = 3
    }

    [Fact]
    public void TryParseHeader_PngRgba_FourChannels()
    {
        byte[] png = MakePngHeader(256, 256, 6); // color type 6 = RGBA

        ImageDimensions? dims = ImageHeaderParser.TryParseHeader(png);

        Assert.NotNull(dims);
        Assert.Equal(256, dims.Width);
        Assert.Equal(256, dims.Height);
        Assert.Equal(4, dims.Channels); // RGBA = 4
    }

    [Fact]
    public void TryParseHeader_PngGrayscale_OneChannel()
    {
        byte[] png = MakePngHeader(64, 64, 0); // color type 0 = Grayscale

        ImageDimensions? dims = ImageHeaderParser.TryParseHeader(png);

        Assert.NotNull(dims);
        Assert.Equal(1, dims.Channels);
    }

    [Fact]
    public void TryParseHeader_UnknownFormat_ReturnsNull()
    {
        byte[] garbage = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09];

        ImageDimensions? dims = ImageHeaderParser.TryParseHeader(garbage);

        Assert.Null(dims);
    }

    [Fact]
    public void TryParseHeader_TooShort_ReturnsNull()
    {
        byte[] tiny = [0xFF, 0xD8];

        ImageDimensions? dims = ImageHeaderParser.TryParseHeader(tiny);

        Assert.Null(dims);
    }

    [Fact]
    public void Add_MultipleJpegs_TracksMinMaxDimensions()
    {
        ImageStatsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromImage(MakeJpegHeader(640, 480, 3)));
        accumulator.Add(DataValue.FromImage(MakeJpegHeader(1920, 1080, 3)));
        accumulator.Add(DataValue.FromImage(MakeJpegHeader(320, 240, 3)));

        ImageStatsResult result = (ImageStatsResult)accumulator.GetResult().Value!;
        Assert.Equal(3, result.ImageCount);
        Assert.Equal(320, result.MinWidth);
        Assert.Equal(1920, result.MaxWidth);
        Assert.Equal(240, result.MinHeight);
        Assert.Equal(1080, result.MaxHeight);
    }

    [Fact]
    public void Add_MixedChannels_TracksDistribution()
    {
        ImageStatsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromImage(MakeJpegHeader(100, 100, 3)));
        accumulator.Add(DataValue.FromImage(MakeJpegHeader(100, 100, 3)));
        accumulator.Add(DataValue.FromImage(MakeJpegHeader(100, 100, 1)));

        ImageStatsResult result = (ImageStatsResult)accumulator.GetResult().Value!;
        Assert.Equal(2, result.ChannelCounts[3]);
        Assert.Equal(1, result.ChannelCounts[1]);
    }

    [Fact]
    public void Add_TracksFileSizeStats()
    {
        ImageStatsAccumulator accumulator = new();

        byte[] small = MakeJpegHeader(100, 100, 3); // 20 bytes
        byte[] large = new byte[1000];
        Array.Copy(MakeJpegHeader(200, 200, 3), large, 20);

        accumulator.Add(DataValue.FromImage(small));
        accumulator.Add(DataValue.FromImage(large));

        ImageStatsResult result = (ImageStatsResult)accumulator.GetResult().Value!;
        Assert.Equal(20.0, result.FileSizeStats.Min);
        Assert.Equal(1000.0, result.FileSizeStats.Max);
        Assert.Equal(2, result.FileSizeStats.Count);
    }

    [Fact]
    public void Add_UndecodableFormat_CountsAsUndecodable()
    {
        ImageStatsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromImage([0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09]));

        ImageStatsResult result = (ImageStatsResult)accumulator.GetResult().Value!;
        Assert.Equal(1, result.ImageCount);
        Assert.Equal(1, result.UndecodableCount);
    }

    [Fact]
    public void Add_NullValues_Ignored()
    {
        ImageStatsAccumulator accumulator = new();

        accumulator.Add(DataValue.Null(DataKind.Image));

        ImageStatsResult result = (ImageStatsResult)accumulator.GetResult().Value!;
        Assert.Equal(0, result.ImageCount);
    }

    [Fact]
    public void Merge_TwoAccumulators_CombinesCorrectly()
    {
        ImageStatsAccumulator first = new();
        ImageStatsAccumulator second = new();

        first.Add(DataValue.FromImage(MakeJpegHeader(640, 480, 3)));
        second.Add(DataValue.FromImage(MakeJpegHeader(1920, 1080, 3)));

        first.Merge(second);

        ImageStatsResult result = (ImageStatsResult)first.GetResult().Value!;
        Assert.Equal(2, result.ImageCount);
        Assert.Equal(640, result.MinWidth);
        Assert.Equal(1920, result.MaxWidth);
        Assert.Equal(480, result.MinHeight);
        Assert.Equal(1080, result.MaxHeight);
    }

    [Fact]
    public void GetResult_HasCorrectName()
    {
        ImageStatsAccumulator accumulator = new();
        Assert.Equal("image_stats", accumulator.GetResult().Name);
    }

    [Fact]
    public void Add_MultipleImages_BuildsAspectRatioHistogram()
    {
        ImageStatsAccumulator accumulator = new();

        // Landscape (800/600 ≈ 1.333), Portrait (600/800 = 0.75), Square (500/500 = 1.0)
        accumulator.Add(DataValue.FromImage(MakeJpegHeader(800, 600, 3)));
        accumulator.Add(DataValue.FromImage(MakeJpegHeader(600, 800, 3)));
        accumulator.Add(DataValue.FromImage(MakeJpegHeader(500, 500, 3)));

        ImageStatsResult result = (ImageStatsResult)accumulator.GetResult().Value!;

        Assert.NotNull(result.AspectRatioHistogram);
        Assert.True(result.AspectRatioHistogram.Counts.Count > 0);
        Assert.Equal(3, result.AspectRatioHistogram.Counts.Sum());
    }

    [Fact]
    public void Add_SingleImage_AspectRatioHistogramSingleBin()
    {
        ImageStatsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromImage(MakeJpegHeader(640, 480, 3)));

        ImageStatsResult result = (ImageStatsResult)accumulator.GetResult().Value!;

        Assert.NotNull(result.AspectRatioHistogram);
        Assert.Single(result.AspectRatioHistogram.Counts);
        Assert.Equal(1, result.AspectRatioHistogram.Counts[0]);
    }

    [Fact]
    public void Merge_TwoAccumulators_CombinesAspectRatioSamples()
    {
        ImageStatsAccumulator first = new();
        ImageStatsAccumulator second = new();

        first.Add(DataValue.FromImage(MakeJpegHeader(800, 600, 3)));  // landscape
        second.Add(DataValue.FromImage(MakeJpegHeader(600, 800, 3))); // portrait

        first.Merge(second);

        ImageStatsResult result = (ImageStatsResult)first.GetResult().Value!;

        Assert.NotNull(result.AspectRatioHistogram);
        Assert.Equal(2, result.AspectRatioHistogram.Counts.Sum());
    }

    [Fact]
    public void Add_UndecodableImages_NoAspectRatioContribution()
    {
        ImageStatsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromImage([0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09]));

        ImageStatsResult result = (ImageStatsResult)accumulator.GetResult().Value!;

        Assert.Null(result.AspectRatioHistogram);
        Assert.Equal(0, result.AspectRatioStats.Count);
    }

    [Fact]
    public void GetResult_NoDecodableImages_NullAspectRatioHistogram()
    {
        ImageStatsAccumulator accumulator = new();

        ImageStatsResult result = (ImageStatsResult)accumulator.GetResult().Value!;

        Assert.Null(result.AspectRatioHistogram);
        Assert.Equal(0, result.AspectRatioStats.Count);
    }

    [Fact]
    public void Add_MultipleImages_TracksAspectRatioStats()
    {
        ImageStatsAccumulator accumulator = new();

        // Landscape 800/600 ≈ 1.333, Portrait 600/800 = 0.75, Square 500/500 = 1.0
        accumulator.Add(DataValue.FromImage(MakeJpegHeader(800, 600, 3)));
        accumulator.Add(DataValue.FromImage(MakeJpegHeader(600, 800, 3)));
        accumulator.Add(DataValue.FromImage(MakeJpegHeader(500, 500, 3)));

        ImageStatsResult result = (ImageStatsResult)accumulator.GetResult().Value!;

        Assert.Equal(3, result.AspectRatioStats.Count);
        Assert.Equal(0.75, result.AspectRatioStats.Min, 2);
        Assert.Equal(800.0 / 600.0, result.AspectRatioStats.Max, 2);
        Assert.True(result.AspectRatioStats.Mean > 0.9 && result.AspectRatioStats.Mean < 1.1);
        Assert.True(result.AspectRatioStats.StandardDeviation > 0);
    }

    [Fact]
    public void Add_SingleImage_AspectRatioStatsHasZeroVariance()
    {
        ImageStatsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromImage(MakeJpegHeader(640, 480, 3)));

        ImageStatsResult result = (ImageStatsResult)accumulator.GetResult().Value!;

        Assert.Equal(1, result.AspectRatioStats.Count);
        Assert.Equal(640.0 / 480.0, result.AspectRatioStats.Min, 3);
        Assert.Equal(640.0 / 480.0, result.AspectRatioStats.Max, 3);
        Assert.Equal(640.0 / 480.0, result.AspectRatioStats.Mean, 3);
        Assert.Equal(0.0, result.AspectRatioStats.Variance);
    }

    [Fact]
    public void Merge_TwoAccumulators_CombinesAspectRatioStats()
    {
        ImageStatsAccumulator first = new();
        ImageStatsAccumulator second = new();

        first.Add(DataValue.FromImage(MakeJpegHeader(800, 600, 3)));  // ~1.333
        second.Add(DataValue.FromImage(MakeJpegHeader(600, 800, 3))); // 0.75

        first.Merge(second);

        ImageStatsResult result = (ImageStatsResult)first.GetResult().Value!;

        Assert.Equal(2, result.AspectRatioStats.Count);
        Assert.Equal(0.75, result.AspectRatioStats.Min, 2);
        Assert.Equal(800.0 / 600.0, result.AspectRatioStats.Max, 2);
        Assert.True(result.AspectRatioStats.StandardDeviation > 0);
    }

    [Fact]
    public void JpegWithPrecedingMarkers_ParsesCorrectly()
    {
        // JPEG with APP0 marker (JFIF) before SOF0
        byte[] data = new byte[40];
        data[0] = 0xFF;
        data[1] = 0xD8; // SOI
        data[2] = 0xFF;
        data[3] = 0xE0; // APP0
        data[4] = 0x00;
        data[5] = 0x10; // length 16 (14 bytes of data after length)

        // Fill APP0 data
        for (int i = 6; i < 20; i++)
        {
            data[i] = 0x00;
        }

        // SOF0 at offset 20
        data[20] = 0xFF;
        data[21] = 0xC0; // SOF0
        data[22] = 0x00;
        data[23] = 0x0B;
        data[24] = 0x08; // precision
        data[25] = 0x02; // height high byte
        data[26] = 0x00; // height low byte = 512
        data[27] = 0x03; // width high byte
        data[28] = 0x00; // width low byte = 768
        data[29] = 0x03; // 3 channels

        ImageDimensions? dims = ImageHeaderParser.TryParseHeader(data);

        Assert.NotNull(dims);
        Assert.Equal(768, dims.Width);
        Assert.Equal(512, dims.Height);
        Assert.Equal(3, dims.Channels);
    }

    [Fact]
    public void Add_MultipleImages_TracksMegapixelStats()
    {
        ImageStatsAccumulator accumulator = new();

        // 800×600 = 0.48 MP, 1920×1080 = 2.0736 MP, 320×240 = 0.0768 MP
        accumulator.Add(DataValue.FromImage(MakeJpegHeader(800, 600, 3)));
        accumulator.Add(DataValue.FromImage(MakeJpegHeader(1920, 1080, 3)));
        accumulator.Add(DataValue.FromImage(MakeJpegHeader(320, 240, 3)));

        ImageStatsResult result = (ImageStatsResult)accumulator.GetResult().Value!;

        Assert.Equal(3, result.MegapixelStats.Count);
        Assert.Equal(320.0 * 240 / 1_000_000.0, result.MegapixelStats.Min, 4);
        Assert.Equal(1920.0 * 1080 / 1_000_000.0, result.MegapixelStats.Max, 4);
        Assert.True(result.MegapixelStats.Mean > 0);
        Assert.True(result.MegapixelStats.StandardDeviation > 0);
    }

    [Fact]
    public void Merge_TwoAccumulators_CombinesMegapixelStats()
    {
        ImageStatsAccumulator first = new();
        ImageStatsAccumulator second = new();

        first.Add(DataValue.FromImage(MakeJpegHeader(640, 480, 3)));   // 0.3072 MP
        second.Add(DataValue.FromImage(MakeJpegHeader(1920, 1080, 3))); // 2.0736 MP

        first.Merge(second);

        ImageStatsResult result = (ImageStatsResult)first.GetResult().Value!;

        Assert.Equal(2, result.MegapixelStats.Count);
        Assert.Equal(640.0 * 480 / 1_000_000.0, result.MegapixelStats.Min, 4);
        Assert.Equal(1920.0 * 1080 / 1_000_000.0, result.MegapixelStats.Max, 4);
        Assert.True(result.MegapixelStats.StandardDeviation > 0);
    }

    [Fact]
    public void Add_UndecodableImages_NoMegapixelContribution()
    {
        ImageStatsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromImage([0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09]));

        ImageStatsResult result = (ImageStatsResult)accumulator.GetResult().Value!;

        Assert.Equal(0, result.MegapixelStats.Count);
    }

    [Fact]
    public void Add_MixedOrientations_TracksDistribution()
    {
        ImageStatsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromImage(MakeJpegHeader(800, 600, 3)));  // landscape
        accumulator.Add(DataValue.FromImage(MakeJpegHeader(600, 800, 3)));  // portrait
        accumulator.Add(DataValue.FromImage(MakeJpegHeader(500, 500, 3)));  // square
        accumulator.Add(DataValue.FromImage(MakeJpegHeader(1920, 1080, 3))); // landscape

        ImageStatsResult result = (ImageStatsResult)accumulator.GetResult().Value!;

        Assert.Equal(2, result.OrientationCounts["landscape"]);
        Assert.Equal(1, result.OrientationCounts["portrait"]);
        Assert.Equal(1, result.OrientationCounts["square"]);
    }

    [Fact]
    public void Merge_TwoAccumulators_CombinesOrientationCounts()
    {
        ImageStatsAccumulator first = new();
        ImageStatsAccumulator second = new();

        first.Add(DataValue.FromImage(MakeJpegHeader(800, 600, 3)));  // landscape
        first.Add(DataValue.FromImage(MakeJpegHeader(500, 500, 3)));  // square
        second.Add(DataValue.FromImage(MakeJpegHeader(600, 800, 3))); // portrait
        second.Add(DataValue.FromImage(MakeJpegHeader(1920, 1080, 3))); // landscape

        first.Merge(second);

        ImageStatsResult result = (ImageStatsResult)first.GetResult().Value!;

        Assert.Equal(2, result.OrientationCounts["landscape"]);
        Assert.Equal(1, result.OrientationCounts["portrait"]);
        Assert.Equal(1, result.OrientationCounts["square"]);
    }

    [Fact]
    public void Add_UndecodableImages_NoOrientationContribution()
    {
        ImageStatsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromImage([0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09]));

        ImageStatsResult result = (ImageStatsResult)accumulator.GetResult().Value!;

        Assert.Empty(result.OrientationCounts);
    }

    [Fact]
    public void Add_TinyImages_CountsCorrectly()
    {
        ImageStatsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromImage(MakeJpegHeader(16, 16, 3)));   // both dims < 32
        accumulator.Add(DataValue.FromImage(MakeJpegHeader(31, 100, 3)));  // width < 32
        accumulator.Add(DataValue.FromImage(MakeJpegHeader(100, 20, 3)));  // height < 32
        accumulator.Add(DataValue.FromImage(MakeJpegHeader(640, 480, 3))); // normal

        ImageStatsResult result = (ImageStatsResult)accumulator.GetResult().Value!;

        Assert.Equal(3, result.TinyImageCount);
        Assert.Equal(0, result.HugeImageCount);
    }

    [Fact]
    public void Add_HugeImages_CountsCorrectly()
    {
        ImageStatsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromImage(MakeJpegHeader(5000, 3000, 3))); // width > 4096
        accumulator.Add(DataValue.FromImage(MakeJpegHeader(3000, 5000, 3))); // height > 4096
        accumulator.Add(DataValue.FromImage(MakeJpegHeader(640, 480, 3)));   // normal

        ImageStatsResult result = (ImageStatsResult)accumulator.GetResult().Value!;

        Assert.Equal(2, result.HugeImageCount);
        Assert.Equal(0, result.TinyImageCount);
    }

    [Fact]
    public void Add_NormalImages_ZeroExtremeCounts()
    {
        ImageStatsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromImage(MakeJpegHeader(640, 480, 3)));
        accumulator.Add(DataValue.FromImage(MakeJpegHeader(1920, 1080, 3)));
        accumulator.Add(DataValue.FromImage(MakeJpegHeader(256, 256, 3)));

        ImageStatsResult result = (ImageStatsResult)accumulator.GetResult().Value!;

        Assert.Equal(0, result.TinyImageCount);
        Assert.Equal(0, result.HugeImageCount);
    }

    [Fact]
    public void Merge_TwoAccumulators_CombinesExtremeCounts()
    {
        ImageStatsAccumulator first = new();
        ImageStatsAccumulator second = new();

        first.Add(DataValue.FromImage(MakeJpegHeader(16, 16, 3)));    // tiny
        first.Add(DataValue.FromImage(MakeJpegHeader(5000, 3000, 3))); // huge
        second.Add(DataValue.FromImage(MakeJpegHeader(10, 10, 3)));   // tiny
        second.Add(DataValue.FromImage(MakeJpegHeader(8000, 6000, 3))); // huge

        first.Merge(second);

        ImageStatsResult result = (ImageStatsResult)first.GetResult().Value!;

        Assert.Equal(2, result.TinyImageCount);
        Assert.Equal(2, result.HugeImageCount);
    }

    [Fact]
    public void Add_MultipleImages_TracksPixelCountStats()
    {
        ImageStatsAccumulator accumulator = new();

        // 800×600 = 480,000 px, 1920×1080 = 2,073,600 px, 320×240 = 76,800 px
        accumulator.Add(DataValue.FromImage(MakeJpegHeader(800, 600, 3)));
        accumulator.Add(DataValue.FromImage(MakeJpegHeader(1920, 1080, 3)));
        accumulator.Add(DataValue.FromImage(MakeJpegHeader(320, 240, 3)));

        ImageStatsResult result = (ImageStatsResult)accumulator.GetResult().Value!;

        Assert.Equal(3, result.PixelCountStats.Count);
        Assert.Equal(76_800.0, result.PixelCountStats.Min, 0);
        Assert.Equal(2_073_600.0, result.PixelCountStats.Max, 0);
        Assert.True(result.PixelCountStats.Mean > 0);
        Assert.True(result.PixelCountStats.StandardDeviation > 0);
    }

    [Fact]
    public void Merge_TwoAccumulators_CombinesPixelCountStats()
    {
        ImageStatsAccumulator first = new();
        ImageStatsAccumulator second = new();

        first.Add(DataValue.FromImage(MakeJpegHeader(640, 480, 3)));   // 307,200 px
        second.Add(DataValue.FromImage(MakeJpegHeader(1920, 1080, 3))); // 2,073,600 px

        first.Merge(second);

        ImageStatsResult result = (ImageStatsResult)first.GetResult().Value!;

        Assert.Equal(2, result.PixelCountStats.Count);
        Assert.Equal(307_200.0, result.PixelCountStats.Min, 0);
        Assert.Equal(2_073_600.0, result.PixelCountStats.Max, 0);
        Assert.True(result.PixelCountStats.StandardDeviation > 0);
    }

    [Fact]
    public void Add_SingleImage_PixelCountStatsHasZeroVariance()
    {
        ImageStatsAccumulator accumulator = new();

        accumulator.Add(DataValue.FromImage(MakeJpegHeader(640, 480, 3)));

        ImageStatsResult result = (ImageStatsResult)accumulator.GetResult().Value!;

        Assert.Equal(1, result.PixelCountStats.Count);
        Assert.Equal(307_200.0, result.PixelCountStats.Min, 0);
        Assert.Equal(307_200.0, result.PixelCountStats.Max, 0);
        Assert.Equal(307_200.0, result.PixelCountStats.Mean, 0);
        Assert.Equal(0.0, result.PixelCountStats.Variance);
    }
}
