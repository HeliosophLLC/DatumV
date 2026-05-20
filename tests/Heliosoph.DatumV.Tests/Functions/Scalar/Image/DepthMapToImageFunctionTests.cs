using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Image;
using Heliosoph.DatumV.Model;

using SkiaSharp;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Image;

/// <summary>
/// Covers <c>depth_map_to_image(values, source_h, source_w, target_h,
/// target_w)</c> — the post-processing scalar shared by MiDaS / DPT
/// depth bodies. Validates the min-max normalize math, grayscale pack,
/// and resize-to-target output dimensions.
/// </summary>
public sealed class DepthMapToImageFunctionTests : ServiceTestBase
{
    private async Task<SKBitmap> InvokeAsync(
        float[] values, int sourceH, int sourceW, int targetH, int targetW)
    {
        DepthMapToImageFunction fn = new();
        ValueRef result = await fn.ExecuteAsync(
            new ValueRef[]
            {
                ValueRef.FromPrimitiveArray(values, DataKind.Float32),
                ValueRef.FromInt32(sourceH),
                ValueRef.FromInt32(sourceW),
                ValueRef.FromInt32(targetH),
                ValueRef.FromInt32(targetW),
            }.AsMemory(),
            CreateEvaluationFrame(), CancellationToken.None);
        Assert.Equal(DataKind.Image, result.Kind);
        Assert.False(result.IsNull);
        return result.AsImage();
    }

    [Fact]
    public async Task IdentitySize_NormalizesToFullRange()
    {
        // 2×2 input with values [0, 1, 2, 3] — min=0, max=3.
        // Post min-max normalize: [0, 1/3, 2/3, 1] → bytes [0, 85, 170, 255].
        float[] values = [0f, 1f, 2f, 3f];
        SKBitmap result = await InvokeAsync(values, sourceH: 2, sourceW: 2, targetH: 2, targetW: 2);

        Assert.Equal(2, result.Width);
        Assert.Equal(2, result.Height);

        // Pixel(0,0) → 0 (min) → byte 0.
        SKColor topLeft = result.GetPixel(0, 0);
        Assert.Equal(0, topLeft.Red);
        Assert.Equal(0, topLeft.Green);
        Assert.Equal(0, topLeft.Blue);
        Assert.Equal(255, topLeft.Alpha);

        // Pixel(1,1) → 3 (max) → byte 255.
        SKColor bottomRight = result.GetPixel(1, 1);
        Assert.Equal(255, bottomRight.Red);
        Assert.Equal(255, bottomRight.Green);
        Assert.Equal(255, bottomRight.Blue);
    }

    [Fact]
    public async Task UpscalesToTargetDimensions()
    {
        // 2×2 input → 8×8 target. Bilinear resize means corners stay
        // ~unchanged but interior pixels interpolate.
        float[] values = [0f, 1f, 2f, 3f];
        SKBitmap result = await InvokeAsync(values, sourceH: 2, sourceW: 2, targetH: 8, targetW: 8);

        Assert.Equal(8, result.Width);
        Assert.Equal(8, result.Height);

        // Corner pixels should still be near min/max post-resize.
        Assert.True(result.GetPixel(0, 0).Red < 32, "top-left should be ~black");
        Assert.True(result.GetPixel(7, 7).Red > 224, "bottom-right should be ~white");
    }

    [Fact]
    public async Task UniformInput_EmitsFlatBlack()
    {
        // All values equal → range collapses to 0 → divide-by-zero guard
        // kicks in, output is uniform black (after normalize each pixel
        // is (v - v) / 1 = 0).
        float[] values = [5f, 5f, 5f, 5f];
        SKBitmap result = await InvokeAsync(values, sourceH: 2, sourceW: 2, targetH: 2, targetW: 2);

        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                SKColor c = result.GetPixel(x, y);
                Assert.Equal(0, c.Red);
            }
        }
    }

    [Fact]
    public async Task WrongValuesLength_ThrowsFunctionArgumentException()
    {
        DepthMapToImageFunction fn = new();
        await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await fn.ExecuteAsync(
                new ValueRef[]
                {
                    ValueRef.FromPrimitiveArray(new float[10], DataKind.Float32),
                    ValueRef.FromInt32(4),
                    ValueRef.FromInt32(4),
                    ValueRef.FromInt32(4),
                    ValueRef.FromInt32(4),
                }.AsMemory(),
                CreateEvaluationFrame(), CancellationToken.None));
    }

    [Fact]
    public async Task NullValues_ReturnsNullImage()
    {
        DepthMapToImageFunction fn = new();
        ValueRef result = await fn.ExecuteAsync(
            new ValueRef[]
            {
                ValueRef.NullArray(DataKind.Float32),
                ValueRef.FromInt32(2),
                ValueRef.FromInt32(2),
                ValueRef.FromInt32(2),
                ValueRef.FromInt32(2),
            }.AsMemory(),
            CreateEvaluationFrame(), CancellationToken.None);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Image, result.Kind);
    }
}
