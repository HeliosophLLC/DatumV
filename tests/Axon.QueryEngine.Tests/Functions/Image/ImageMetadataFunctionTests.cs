namespace Axon.QueryEngine.Tests.Functions.Image;

using Axon.QueryEngine.Functions.Image;
using Axon.QueryEngine.Model;

/// <summary>
/// Tests for header-only image metadata functions:
/// <see cref="ImageWidthFunction"/>, <see cref="ImageHeightFunction"/>,
/// <see cref="ImageChannelsFunction"/>, <see cref="ImagePixelCountFunction"/>,
/// and <see cref="ImageDimensionsFunction"/>.
/// </summary>
public sealed class ImageMetadataFunctionTests
{
    // ───────────────── Helpers ─────────────────

    /// <summary>Builds a minimal valid JPEG header with the given dimensions and channel count.</summary>
    private static byte[] MakeJpegHeader(int width, int height, int channels)
    {
        byte[] data = new byte[20];
        data[0] = 0xFF;
        data[1] = 0xD8; // SOI

        data[2] = 0xFF;
        data[3] = 0xC0; // SOF0
        data[4] = 0x00;
        data[5] = 0x0B; // length = 11
        data[6] = 0x08; // precision
        data[7] = (byte)(height >> 8);
        data[8] = (byte)(height & 0xFF);
        data[9] = (byte)(width >> 8);
        data[10] = (byte)(width & 0xFF);
        data[11] = (byte)channels;

        return data;
    }

    /// <summary>Builds a minimal valid PNG header with the given dimensions and color type.</summary>
    private static byte[] MakePngHeader(int width, int height, byte colorType)
    {
        byte[] data = new byte[30];
        // PNG signature
        data[0] = 0x89; data[1] = 0x50; data[2] = 0x4E; data[3] = 0x47;
        data[4] = 0x0D; data[5] = 0x0A; data[6] = 0x1A; data[7] = 0x0A;

        // IHDR chunk length
        data[8] = 0x00; data[9] = 0x00; data[10] = 0x00; data[11] = 0x0D;

        // IHDR type
        data[12] = (byte)'I'; data[13] = (byte)'H'; data[14] = (byte)'D'; data[15] = (byte)'R';

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

        data[24] = 8;          // bit depth
        data[25] = colorType;

        return data;
    }

    // ───────────────── ImageWidthFunction ─────────────────

    private readonly ImageWidthFunction _width = new();

    [Fact]
    public void Width_Name()
    {
        Assert.Equal("width", _width.Name);
    }

    [Fact]
    public void Width_Validate_AcceptsImageOrUInt8Array()
    {
        Assert.Equal(DataKind.Scalar, _width.ValidateArguments([DataKind.Image]));
        Assert.Equal(DataKind.Scalar, _width.ValidateArguments([DataKind.UInt8Array]));
    }

    [Fact]
    public void Width_Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() => _width.ValidateArguments([]));
        Assert.Throws<ArgumentException>(() => _width.ValidateArguments([DataKind.Image, DataKind.Scalar]));
    }

    [Fact]
    public void Width_Validate_WrongType_Throws()
    {
        Assert.Throws<ArgumentException>(() => _width.ValidateArguments([DataKind.Scalar]));
    }

    [Fact]
    public void Width_Jpeg_ReturnsWidth()
    {
        byte[] jpeg = MakeJpegHeader(640, 480, 3);
        DataValue result = _width.Execute([DataValue.FromImage(jpeg)]);
        Assert.Equal(640f, result.AsScalar());
    }

    [Fact]
    public void Width_Png_ReturnsWidth()
    {
        byte[] png = MakePngHeader(1920, 1080, 2); // RGB
        DataValue result = _width.Execute([DataValue.FromImage(png)]);
        Assert.Equal(1920f, result.AsScalar());
    }

    [Fact]
    public void Width_NullInput_ReturnsNull()
    {
        DataValue result = _width.Execute([DataValue.Null(DataKind.Image)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── ImageHeightFunction ─────────────────

    private readonly ImageHeightFunction _height = new();

    [Fact]
    public void Height_Name()
    {
        Assert.Equal("height", _height.Name);
    }

    [Fact]
    public void Height_Jpeg_ReturnsHeight()
    {
        byte[] jpeg = MakeJpegHeader(640, 480, 3);
        DataValue result = _height.Execute([DataValue.FromImage(jpeg)]);
        Assert.Equal(480f, result.AsScalar());
    }

    [Fact]
    public void Height_Png_ReturnsHeight()
    {
        byte[] png = MakePngHeader(320, 240, 2);
        DataValue result = _height.Execute([DataValue.FromImage(png)]);
        Assert.Equal(240f, result.AsScalar());
    }

    [Fact]
    public void Height_NullInput_ReturnsNull()
    {
        DataValue result = _height.Execute([DataValue.Null(DataKind.Image)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── ImageChannelsFunction ─────────────────

    private readonly ImageChannelsFunction _channels = new();

    [Fact]
    public void Channels_Name()
    {
        Assert.Equal("channels", _channels.Name);
    }

    [Fact]
    public void Channels_Jpeg_3Channels()
    {
        byte[] jpeg = MakeJpegHeader(100, 100, 3);
        DataValue result = _channels.Execute([DataValue.FromImage(jpeg)]);
        Assert.Equal(3f, result.AsScalar());
    }

    [Fact]
    public void Channels_Jpeg_1Channel()
    {
        byte[] jpeg = MakeJpegHeader(100, 100, 1);
        DataValue result = _channels.Execute([DataValue.FromImage(jpeg)]);
        Assert.Equal(1f, result.AsScalar());
    }

    [Fact]
    public void Channels_Png_RGBA()
    {
        byte[] png = MakePngHeader(100, 100, 6); // RGBA
        DataValue result = _channels.Execute([DataValue.FromImage(png)]);
        Assert.Equal(4f, result.AsScalar());
    }

    [Fact]
    public void Channels_NullInput_ReturnsNull()
    {
        DataValue result = _channels.Execute([DataValue.Null(DataKind.Image)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── ImagePixelCountFunction ─────────────────

    private readonly ImagePixelCountFunction _pixelCount = new();

    [Fact]
    public void PixelCount_Name()
    {
        Assert.Equal("pixel_count", _pixelCount.Name);
    }

    [Fact]
    public void PixelCount_Jpeg()
    {
        byte[] jpeg = MakeJpegHeader(640, 480, 3);
        DataValue result = _pixelCount.Execute([DataValue.FromImage(jpeg)]);
        Assert.Equal(640f * 480f, result.AsScalar());
    }

    [Fact]
    public void PixelCount_NullInput_ReturnsNull()
    {
        DataValue result = _pixelCount.Execute([DataValue.Null(DataKind.Image)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── ImageDimensionsFunction ─────────────────

    private readonly ImageDimensionsFunction _dimensions = new();

    [Fact]
    public void Dimensions_Name()
    {
        Assert.Equal("dimensions", _dimensions.Name);
    }

    [Fact]
    public void Dimensions_Validate_AcceptsImageAndString()
    {
        Assert.Equal(DataKind.Vector, _dimensions.ValidateArguments([DataKind.Image, DataKind.String]));
    }

    [Fact]
    public void Dimensions_HWC_Format()
    {
        byte[] jpeg = MakeJpegHeader(640, 480, 3);
        DataValue result = _dimensions.Execute([DataValue.FromImage(jpeg), DataValue.FromString("HWC")]);
        float[] vector = result.AsVector();
        Assert.Equal(3, vector.Length);
        Assert.Equal(480f, vector[0]); // H
        Assert.Equal(640f, vector[1]); // W
        Assert.Equal(3f, vector[2]);   // C
    }

    [Fact]
    public void Dimensions_CHW_Format()
    {
        byte[] jpeg = MakeJpegHeader(640, 480, 3);
        DataValue result = _dimensions.Execute([DataValue.FromImage(jpeg), DataValue.FromString("CHW")]);
        float[] vector = result.AsVector();
        Assert.Equal(3, vector.Length);
        Assert.Equal(3f, vector[0]);   // C
        Assert.Equal(480f, vector[1]); // H
        Assert.Equal(640f, vector[2]); // W
    }

    [Fact]
    public void Dimensions_WH_Format()
    {
        byte[] jpeg = MakeJpegHeader(640, 480, 3);
        DataValue result = _dimensions.Execute([DataValue.FromImage(jpeg), DataValue.FromString("WH")]);
        float[] vector = result.AsVector();
        Assert.Equal(2, vector.Length);
        Assert.Equal(640f, vector[0]); // W
        Assert.Equal(480f, vector[1]); // H
    }

    [Fact]
    public void Dimensions_WHC_Format()
    {
        byte[] png = MakePngHeader(320, 240, 6); // RGBA → 4 channels
        DataValue result = _dimensions.Execute([DataValue.FromImage(png), DataValue.FromString("WHC")]);
        float[] vector = result.AsVector();
        Assert.Equal(3, vector.Length);
        Assert.Equal(320f, vector[0]); // W
        Assert.Equal(240f, vector[1]); // H
        Assert.Equal(4f, vector[2]);   // C
    }

    [Fact]
    public void Dimensions_InvalidFormat_Throws()
    {
        byte[] jpeg = MakeJpegHeader(640, 480, 3);
        Assert.Throws<ArgumentException>(() =>
            _dimensions.Execute([DataValue.FromImage(jpeg), DataValue.FromString("XYZ")]));
    }

    [Fact]
    public void Dimensions_NullInput_ReturnsNull()
    {
        DataValue result = _dimensions.Execute([DataValue.Null(DataKind.Image), DataValue.FromString("HWC")]);
        Assert.True(result.IsNull);
    }
}
