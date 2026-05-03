using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Image;

using SkiaSharp;

namespace DatumIngest.Tests.Functions.Scalar.Image;

/// <summary>
/// Augmentation transforms: noise, resize_and_crop,
/// affine_transform, elastic_deform, perspective_warp.
/// </summary>
public sealed class AugmentationTransformsTests : ServiceTestBase
{
    // ----- noise -----

    [Fact]
    public async Task Noise_TwoArg_DefaultsToGaussian_PreservesDimensions()
    {
        ValueRef result = await new NoiseImageFunction().ExecuteAsync(
            new[] { MakeSolid(16, 16, 128, 128, 128), ValueRef.FromFloat32(20) },
            CreateEvaluationFrame(), default);
        SKBitmap bmp = result.AsImage();
        Assert.Equal(16, bmp.Width);
        Assert.Equal(16, bmp.Height);
    }

    [Fact]
    public async Task Noise_Gaussian_PerturbsPixels()
    {
        // Stddev 50 should produce a clearly non-uniform image from a solid grey.
        ValueRef result = await new NoiseImageFunction().ExecuteAsync(
            new[] { MakeSolid(16, 16, 128, 128, 128), ValueRef.FromFloat32(50) },
            CreateEvaluationFrame(), default);
        Assert.True(HasPerturbation(result.AsImage(), 128));
    }

    [Fact]
    public async Task Noise_SaltPepper_FlipsSomePixelsToExtremes()
    {
        ValueRef result = await new NoiseImageFunction().ExecuteAsync(
            new[]
            {
                MakeSolid(32, 32, 128, 128, 128),
                ValueRef.FromString("salt_pepper"),
                ValueRef.FromFloat32(1.0f),  // 100% — every pixel gets flipped
            },
            CreateEvaluationFrame(), default);
        SKBitmap bmp = result.AsImage();
        int extremeCount = 0;
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                SKColor px = bmp.GetPixel(x, y);
                if (px.Red == 0 || px.Red == 255) extremeCount++;
            }
        Assert.Equal(32 * 32, extremeCount);
    }

    [Fact]
    public async Task Noise_UnknownType_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new NoiseImageFunction().ExecuteAsync(
                new[]
                {
                    MakeSolid(4, 4, 0, 0, 0),
                    ValueRef.FromString("uniform"),
                    ValueRef.FromFloat32(1.0f),
                },
                CreateEvaluationFrame(), default));
    }

    [Fact]
    public async Task Noise_IsNonPure()
    {
        Assert.False(new NoiseImageFunction().IsPure);
    }

    // ----- resize_and_crop -----

    [Fact]
    public async Task ResizeAndCrop_ProducesRequestedExactDimensions()
    {
        // 32×16 source, want 8×8 output with center gravity.
        ValueRef result = await new ResizeAndCropImageFunction().ExecuteAsync(
            new[]
            {
                MakeSolid(32, 16, 50, 50, 50),
                ValueRef.FromInt32(8), ValueRef.FromInt32(8),
                ValueRef.FromString("center"),
            },
            CreateEvaluationFrame(), default);
        SKBitmap bmp = result.AsImage();
        Assert.Equal(8, bmp.Width);
        Assert.Equal(8, bmp.Height);
    }

    [Fact]
    public async Task ResizeAndCrop_GravityAffectsOutput()
    {
        // Source has left half black, right half white. Gravity should pick which side survives.
        SKBitmap source = new(new SKImageInfo(32, 16, SKColorType.Rgba8888, SKAlphaType.Opaque));
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 32; x++)
                source.SetPixel(x, y, x < 16 ? new SKColor(0, 0, 0, 255) : new SKColor(255, 255, 255, 255));

        ValueRef left = await new ResizeAndCropImageFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromImage(source.Copy()),
                ValueRef.FromInt32(8), ValueRef.FromInt32(8),
                ValueRef.FromString("left"),
            },
            CreateEvaluationFrame(), default);
        ValueRef right = await new ResizeAndCropImageFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromImage(source.Copy()),
                ValueRef.FromInt32(8), ValueRef.FromInt32(8),
                ValueRef.FromString("right"),
            },
            CreateEvaluationFrame(), default);

        // Left gravity should preserve the dark side; right should preserve white.
        Assert.True(left.AsImage().GetPixel(4, 4).Red < 64);
        Assert.True(right.AsImage().GetPixel(4, 4).Red > 192);
    }

    [Fact]
    public async Task ResizeAndCrop_BadGravity_Throws()
    {
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new ResizeAndCropImageFunction().ExecuteAsync(
                new[]
                {
                    MakeSolid(8, 8, 0, 0, 0),
                    ValueRef.FromInt32(4), ValueRef.FromInt32(4),
                    ValueRef.FromString("upper_left"),
                },
                CreateEvaluationFrame(), default));
        Assert.Contains("unknown gravity", ex.Message);
    }

    // ----- affine_transform -----

    [Fact]
    public async Task AffineTransform_IdentityParameters_ApproximatesInput()
    {
        // angle=0, scale=1,1, shear=0,0 → identity transform.
        ValueRef result = await new AffineTransformFunction().ExecuteAsync(
            new[]
            {
                MakeSolid(8, 8, 100, 150, 200),
                ValueRef.FromFloat32(0),
                ValueRef.FromFloat32(1), ValueRef.FromFloat32(1),
                ValueRef.FromFloat32(0), ValueRef.FromFloat32(0),
            },
            CreateEvaluationFrame(), default);
        SKColor px = result.AsImage().GetPixel(4, 4);
        Assert.InRange((int)px.Red, 98, 102);
        Assert.InRange((int)px.Green, 148, 152);
        Assert.InRange((int)px.Blue, 198, 202);
    }

    [Fact]
    public async Task AffineTransform_PreservesCanvasDimensions()
    {
        ValueRef result = await new AffineTransformFunction().ExecuteAsync(
            new[]
            {
                MakeSolid(20, 15, 0, 0, 0),
                ValueRef.FromFloat32(45),
                ValueRef.FromFloat32(1), ValueRef.FromFloat32(1),
                ValueRef.FromFloat32(0.2f), ValueRef.FromFloat32(0),
            },
            CreateEvaluationFrame(), default);
        SKBitmap bmp = result.AsImage();
        Assert.Equal(20, bmp.Width);
        Assert.Equal(15, bmp.Height);
    }

    // ----- elastic_deform -----

    [Fact]
    public async Task ElasticDeform_PreservesDimensions()
    {
        ValueRef result = await new ElasticDeformFunction().ExecuteAsync(
            new[]
            {
                MakeSolid(16, 16, 100, 100, 100),
                ValueRef.FromFloat32(8),
                ValueRef.FromFloat32(2),
            },
            CreateEvaluationFrame(), default);
        SKBitmap bmp = result.AsImage();
        Assert.Equal(16, bmp.Width);
        Assert.Equal(16, bmp.Height);
    }

    [Fact]
    public async Task ElasticDeform_SolidImage_StaysSolid()
    {
        // Bilinear sampling of a solid image yields the same solid value
        // everywhere regardless of displacement.
        ValueRef result = await new ElasticDeformFunction().ExecuteAsync(
            new[]
            {
                MakeSolid(16, 16, 100, 100, 100),
                ValueRef.FromFloat32(8),
                ValueRef.FromFloat32(2),
            },
            CreateEvaluationFrame(), default);
        SKColor px = result.AsImage().GetPixel(8, 8);
        Assert.Equal(100, px.Red);
        Assert.Equal(100, px.Green);
        Assert.Equal(100, px.Blue);
    }

    [Fact]
    public async Task ElasticDeform_NonPositiveSigma_Throws()
    {
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new ElasticDeformFunction().ExecuteAsync(
                new[]
                {
                    MakeSolid(8, 8, 0, 0, 0),
                    ValueRef.FromFloat32(8),
                    ValueRef.FromFloat32(0),
                },
                CreateEvaluationFrame(), default));
        Assert.Contains("sigma must be positive", ex.Message);
    }

    [Fact]
    public async Task ElasticDeform_IsNonPure()
    {
        Assert.False(new ElasticDeformFunction().IsPure);
    }

    // ----- perspective_warp -----

    [Fact]
    public async Task PerspectiveWarp_RandomIntensity_PreservesDimensions()
    {
        ValueRef result = await new PerspectiveWarpFunction().ExecuteAsync(
            new[] { MakeSolid(16, 16, 50, 50, 50), ValueRef.FromFloat32(0.1f) },
            CreateEvaluationFrame(), default);
        SKBitmap bmp = result.AsImage();
        Assert.Equal(16, bmp.Width);
        Assert.Equal(16, bmp.Height);
    }

    [Fact]
    public async Task PerspectiveWarp_IdentityCorners_ApproximatesInput()
    {
        // tl=(0,0), tr=(1,0), bl=(0,1), br=(1,1) — destination matches source corners.
        ValueRef result = await new PerspectiveWarpFunction().ExecuteAsync(
            new[]
            {
                MakeSolid(16, 16, 100, 150, 200),
                ValueRef.FromFloat32(0), ValueRef.FromFloat32(0),
                ValueRef.FromFloat32(1), ValueRef.FromFloat32(0),
                ValueRef.FromFloat32(0), ValueRef.FromFloat32(1),
                ValueRef.FromFloat32(1), ValueRef.FromFloat32(1),
            },
            CreateEvaluationFrame(), default);
        SKColor px = result.AsImage().GetPixel(8, 8);
        Assert.InRange((int)px.Red, 98, 102);
        Assert.InRange((int)px.Green, 148, 152);
        Assert.InRange((int)px.Blue, 198, 202);
    }

    [Fact]
    public async Task PerspectiveWarp_IsNonPure()
    {
        Assert.False(new PerspectiveWarpFunction().IsPure);
    }

    // ----- helpers -----

    private static ValueRef MakeSolid(int w, int h, byte r, byte g, byte b)
    {
        SKBitmap bmp = new(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (SKCanvas canvas = new(bmp))
        {
            canvas.Clear(new SKColor(r, g, b, 255));
        }
        return ValueRef.FromImage(bmp);
    }

    private static bool HasPerturbation(SKBitmap bmp, byte baseline)
    {
        int distinct = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                SKColor px = bmp.GetPixel(x, y);
                if (System.Math.Abs(px.Red - baseline) > 5) distinct++;
            }
        return distinct > bmp.Width * bmp.Height / 4;
    }
}
