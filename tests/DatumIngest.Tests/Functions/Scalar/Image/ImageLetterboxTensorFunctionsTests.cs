using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Image;
using DatumIngest.Model;

using SkiaSharp;

namespace DatumIngest.Tests.Functions.Scalar.Image;

/// <summary>
/// Covers <c>image_letterbox_tensor_chw(img, target_size, mean, std, pad_fill)</c>
/// and its NHWC sibling. The two functions share an arg reader and the same
/// underlying resize + normalize math; tests verify both layouts produce the
/// expected per-pixel values for a known input.
/// </summary>
public sealed class ImageLetterboxTensorFunctionsTests
{
    private static SKBitmap SolidBitmap(int width, int height, byte r, byte g, byte b)
    {
        SKBitmap bmp = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKCanvas canvas = new(bmp);
        canvas.Clear(new SKColor(r, g, b));
        return bmp;
    }

    private static ValueRef Float32Array(params float[] values)
        => ValueRef.FromPrimitiveArray(values, DataKind.Float32);

    private static EvaluationFrame Frame() =>
        new(Row.Empty, new Arena(), new Arena(), new MemoryAccountant(), types: new TypeRegistry());

    private static async Task<float[]> InvokeChwAsync(params ValueRef[] args)
    {
        ImageLetterboxTensorChwFunction fn = new();
        ValueRef result = await fn.ExecuteAsync(args.AsMemory(), Frame(), CancellationToken.None);
        Assert.True(result.IsArray);
        Assert.False(result.IsNull);
        return (float[])result.Materialized!;
    }

    private static async Task<float[]> InvokeHwcAsync(params ValueRef[] args)
    {
        ImageLetterboxTensorHwcFunction fn = new();
        ValueRef result = await fn.ExecuteAsync(args.AsMemory(), Frame(), CancellationToken.None);
        Assert.True(result.IsArray);
        Assert.False(result.IsNull);
        return (float[])result.Materialized!;
    }

    // ─── Square input — no padding needed ──────────────────────────────────────

    [Fact]
    public async Task Chw_SquareInput_FillsCanvasWithNoPadding()
    {
        // 4×4 RGB=(100, 200, 50) → 4×4 letterbox. Square in / square out means
        // the ratio is 1.0 and every pixel of the canvas is written by the resize.
        // No padding cells should remain visible.
        using SKBitmap bmp = SolidBitmap(4, 4, 100, 200, 50);

        // Use raw normalization (mean=[0,0,0], std=[1/255]) so the math is just
        // pixel/255. std must be on the [0, 1) scale because we divide pixel/255
        // by std internally — pass std=1 to get the canonical pixel/255.
        float[] result = await InvokeChwAsync(
            ValueRef.FromImage(bmp),
            ValueRef.FromInt32(4),
            Float32Array(0f, 0f, 0f),
            Float32Array(1f, 1f, 1f),
            ValueRef.FromFloat32(999f)); // sentinel pad — should NOT appear

        Assert.Equal(3 * 4 * 4, result.Length);
        const int plane = 4 * 4;
        for (int i = 0; i < plane; i++)
        {
            Assert.Equal(100f / 255f, result[i],             5);
            Assert.Equal(200f / 255f, result[plane + i],     5);
            Assert.Equal( 50f / 255f, result[2 * plane + i], 5);
        }
        // No pad fill anywhere.
        Assert.DoesNotContain(999f, result);
    }

    // ─── Non-square input — padding required ───────────────────────────────────

    [Fact]
    public async Task Chw_WideInput_PadsBottomRowsWithPadFill()
    {
        // 4×2 (W=4, H=2) → 4×4 canvas. Aspect ratio = min(4/4, 4/2) = 1.
        // Scaled to 4×2 inside the 4×4 canvas — top two rows hold the image,
        // bottom two rows hold the pad fill.
        using SKBitmap bmp = SolidBitmap(4, 2, 100, 200, 50);

        float[] result = await InvokeChwAsync(
            ValueRef.FromImage(bmp),
            ValueRef.FromInt32(4),
            Float32Array(0f, 0f, 0f),
            Float32Array(1f, 1f, 1f),
            ValueRef.FromFloat32(0.5f));

        const int plane = 4 * 4;
        // Top 8 cells of R plane (y=0,1; x=0..3): pixel/255.
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(100f / 255f, result[i], 5);
        }
        // Bottom 8 cells of R plane (y=2,3): pad fill.
        for (int i = 8; i < plane; i++)
        {
            Assert.Equal(0.5f, result[i], 5);
        }
        // Same shape on the G plane: pad fill in bottom 8 cells.
        for (int i = 8; i < plane; i++)
        {
            Assert.Equal(0.5f, result[plane + i], 5);
        }
    }

    // ─── ImageNet-style normalization ──────────────────────────────────────────

    [Fact]
    public async Task Chw_AppliesPerChannelMeanStd()
    {
        // 1×1 RGB=(127, 127, 127) padded into a 2×2 canvas. The single source
        // pixel gets aspect-scaled to fit; rounding may smear it across multiple
        // cells but the math `(127/255 - mean)/std` should hold for at least one.
        using SKBitmap bmp = SolidBitmap(1, 1, 127, 127, 127);

        float[] result = await InvokeChwAsync(
            ValueRef.FromImage(bmp),
            ValueRef.FromInt32(2),
            Float32Array(0.485f, 0.456f, 0.406f),
            Float32Array(0.229f, 0.224f, 0.225f),
            ValueRef.FromFloat32(0f));

        float pixel = 127f / 255f;
        float expectedR = (pixel - 0.485f) / 0.229f;
        float expectedG = (pixel - 0.456f) / 0.224f;
        float expectedB = (pixel - 0.406f) / 0.225f;

        // The top-left cell of each plane must hold the normalized pixel.
        Assert.Equal(expectedR, result[0],   3);
        Assert.Equal(expectedG, result[4],   3);
        Assert.Equal(expectedB, result[8],   3);
    }

    // ─── CHW vs HWC layout difference ──────────────────────────────────────────

    [Fact]
    public async Task Hwc_InterleavesChannelsPerPixel()
    {
        // 2×2 RGB=(100, 200, 50). HWC layout = [R,G,B,R,G,B,...].
        // The exact same input through Chw_SquareInput_FillsCanvasWithNoPadding
        // produces channel-major planes.
        using SKBitmap bmp = SolidBitmap(2, 2, 100, 200, 50);

        float[] result = await InvokeHwcAsync(
            ValueRef.FromImage(bmp),
            ValueRef.FromInt32(2),
            Float32Array(0f, 0f, 0f),
            Float32Array(1f, 1f, 1f),
            ValueRef.FromFloat32(0f));

        Assert.Equal(3 * 2 * 2, result.Length);
        // Pixel 0: R,G,B at indices 0,1,2.
        Assert.Equal(100f / 255f, result[0], 5);
        Assert.Equal(200f / 255f, result[1], 5);
        Assert.Equal( 50f / 255f, result[2], 5);
        // Pixel 1: R,G,B at indices 3,4,5.
        Assert.Equal(100f / 255f, result[3], 5);
        Assert.Equal(200f / 255f, result[4], 5);
        Assert.Equal( 50f / 255f, result[5], 5);
    }

    [Fact]
    public async Task Hwc_PaddingFillsPixelsAfterImageRegion()
    {
        // 2×1 → 2×2: image occupies top row (y=0), bottom row (y=1) is pad.
        // HWC layout puts pad in indices 6..11 (the second row of 2 pixels × 3 channels).
        using SKBitmap bmp = SolidBitmap(2, 1, 100, 200, 50);

        float[] result = await InvokeHwcAsync(
            ValueRef.FromImage(bmp),
            ValueRef.FromInt32(2),
            Float32Array(0f, 0f, 0f),
            Float32Array(1f, 1f, 1f),
            ValueRef.FromFloat32(0.25f));

        // Top row: image pixels.
        for (int i = 0; i < 6; i += 3)
        {
            Assert.Equal(100f / 255f, result[i],     5);
            Assert.Equal(200f / 255f, result[i + 1], 5);
            Assert.Equal( 50f / 255f, result[i + 2], 5);
        }
        // Bottom row: pad fill on every channel.
        for (int i = 6; i < 12; i++)
        {
            Assert.Equal(0.25f, result[i], 5);
        }
    }

    // ─── Error paths ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Chw_NullImage_ReturnsNullArray()
    {
        ImageLetterboxTensorChwFunction fn = new();
        ValueRef result = await fn.ExecuteAsync(
            new[]
            {
                ValueRef.NullArray(DataKind.UInt8), // null image
                ValueRef.FromInt32(4),
                Float32Array(0f, 0f, 0f),
                Float32Array(1f, 1f, 1f),
                ValueRef.FromFloat32(0f),
            }.AsMemory(),
            Frame(),
            CancellationToken.None);

        Assert.True(result.IsNull);
        Assert.True(result.IsArray);
    }

    [Fact]
    public async Task Chw_ZeroStdElement_Throws()
    {
        using SKBitmap bmp = SolidBitmap(1, 1, 50, 50, 50);

        await Assert.ThrowsAsync<FunctionArgumentException>(() => InvokeChwAsync(
            ValueRef.FromImage(bmp),
            ValueRef.FromInt32(1),
            Float32Array(0f, 0f, 0f),
            Float32Array(1f, 0f, 1f),
            ValueRef.FromFloat32(0f)));
    }

    [Fact]
    public async Task Chw_NonPositiveTargetSize_Throws()
    {
        using SKBitmap bmp = SolidBitmap(1, 1, 0, 0, 0);

        await Assert.ThrowsAsync<FunctionArgumentException>(() => InvokeChwAsync(
            ValueRef.FromImage(bmp),
            ValueRef.FromInt32(0),
            Float32Array(0f, 0f, 0f),
            Float32Array(1f, 1f, 1f),
            ValueRef.FromFloat32(0f)));
    }

    [Fact]
    public async Task Chw_WrongMeanArity_Throws()
    {
        using SKBitmap bmp = SolidBitmap(1, 1, 50, 50, 50);

        await Assert.ThrowsAsync<FunctionArgumentException>(() => InvokeChwAsync(
            ValueRef.FromImage(bmp),
            ValueRef.FromInt32(1),
            Float32Array(0f, 0f), // 2 elements, expected 3
            Float32Array(1f, 1f, 1f),
            ValueRef.FromFloat32(0f)));
    }

    // ─── YOLOX-shaped use: raw pixels with 114 pad ─────────────────────────────

    [Fact]
    public async Task Chw_YoloxStyle_RawPixelsWith114Pad()
    {
        // YOLOX-style preprocessing: keep raw byte values (no division), pad
        // with 114 (the standard grey). Express raw-pixels-through as
        // mean=[0,0,0], std=[1/255, ...]: the formula (pixel/255 - 0) / (1/255)
        // = pixel reproduces the raw byte. Pad fill = 114 lands directly.
        //
        // 4×2 image (W=4, H=2) into a 4×4 canvas → bottom 2 rows are pad.
        using SKBitmap bmp = SolidBitmap(4, 2, 200, 100, 50);

        float[] result = await InvokeChwAsync(
            ValueRef.FromImage(bmp),
            ValueRef.FromInt32(4),
            Float32Array(0f, 0f, 0f),
            Float32Array(1f / 255f, 1f / 255f, 1f / 255f),
            ValueRef.FromFloat32(114f));

        const int plane = 4 * 4;
        // Top 8 cells of the R plane: raw byte 200.
        for (int i = 0; i < 8; i++) Assert.Equal(200f, result[i], 5);
        // Bottom 8 cells of the R plane: pad fill 114.
        for (int i = 8; i < plane; i++) Assert.Equal(114f, result[i], 5);
        // G/B planes match by their own pixel value / pad value.
        for (int i = 0; i < 8; i++) Assert.Equal(100f, result[plane + i], 5);
        for (int i = 8; i < plane; i++) Assert.Equal(114f, result[plane + i], 5);
    }
}
