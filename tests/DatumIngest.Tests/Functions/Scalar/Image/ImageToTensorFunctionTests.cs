using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Image;
using DatumIngest.Model;

using SkiaSharp;

namespace DatumIngest.Tests.Functions.Scalar.Image;

/// <summary>
/// Covers <c>image_to_tensor_chw(img, target_size [, mean, std])</c> and the four
/// normalization-preset constant functions (<c>imagenet_mean</c>,
/// <c>imagenet_std</c>, <c>clip_mean</c>, <c>clip_std</c>).
/// </summary>
/// <remarks>
/// Preset tests go through the SQL plan since they take no image input.
/// <c>image_to_tensor_chw</c> tests invoke the function directly with a synthetic
/// <see cref="EvaluationFrame"/> — sidesteps the catalog arena-lifecycle
/// plumbing required to feed an IMAGE column through a scan operator and
/// keeps the assertions focused on the math.
/// </remarks>
public sealed class ImageToTensorChwFunctionTests : ServiceTestBase
{
    private static SKBitmap SolidBitmap(int width, int height, byte r, byte g, byte b)
    {
        SKBitmap bmp = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKCanvas canvas = new(bmp);
        canvas.Clear(new SKColor(r, g, b));
        return bmp;
    }


    private static ValueRef Int32Array(params int[] values)
        => ValueRef.FromPrimitiveArray(values, DataKind.Int32);

    private static ValueRef Float32Array(params float[] values)
        => ValueRef.FromPrimitiveArray(values, DataKind.Float32);

    private async Task<float[]> InvokeAsync(params ValueRef[] args)
    {
        ImageToTensorChwFunction fn = new();
        ValueRef result = await fn.ExecuteAsync(
            args.AsMemory(),
            CreateEvaluationFrame(),
            CancellationToken.None);

        Assert.True(result.IsArray, $"Expected array result, got Kind={result.Kind}");
        Assert.False(result.IsNull);
        return (float[])result.Materialized!;
    }

    // ─── Presets (SQL surface) ───────────────────────────────────────────────

    private async Task<float[]> CollectPresetAsync(string sql)
    {
        TableCatalog catalog = CreateCatalog();
        IQueryPlan plan = catalog.Plan(sql);
        float[]? result = null;
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                DataValue cell = batch[i][0];
                Assert.True(cell.IsArray);
                result = cell.AsArraySpan<float>(batch.Arena).ToArray();
            }
        }
        Assert.NotNull(result);
        return result!;
    }

    [Fact]
    public async Task ImagenetMean_ReturnsCanonicalRgbPreset()
    {
        float[] values = await CollectPresetAsync("SELECT imagenet_mean()");
        Assert.Equal([0.485f, 0.456f, 0.406f], values);
    }

    [Fact]
    public async Task ImagenetStd_ReturnsCanonicalRgbPreset()
    {
        float[] values = await CollectPresetAsync("SELECT imagenet_std()");
        Assert.Equal([0.229f, 0.224f, 0.225f], values);
    }

    [Fact]
    public async Task ClipMean_ReturnsCanonicalOpenAiPreset()
    {
        float[] values = await CollectPresetAsync("SELECT clip_mean()");
        Assert.Equal([0.48145466f, 0.4578275f, 0.40821073f], values);
    }

    [Fact]
    public async Task ClipStd_ReturnsCanonicalOpenAiPreset()
    {
        float[] values = await CollectPresetAsync("SELECT clip_std()");
        Assert.Equal([0.26862954f, 0.26130258f, 0.27577711f], values);
    }

    // ─── image_to_tensor_chw (direct invocation) ────────────────────────────

    [Fact]
    public async Task ImageToTensor_TwoArg_DividesPixelsBy255AndLaysOutNCHW()
    {
        // Solid 2×2 RGB=(100, 200, 50). NCHW with N=1 gives a 12-element
        // [R×4, G×4, B×4] layout.
        using SKBitmap bmp = SolidBitmap(2, 2, 100, 200, 50);

        float[] result = await InvokeAsync(ValueRef.FromImage(bmp), Int32Array(2, 2));

        Assert.Equal(12, result.Length);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(100f / 255f, result[i],     5);
            Assert.Equal(200f / 255f, result[i + 4], 5);
            Assert.Equal( 50f / 255f, result[i + 8], 5);
        }
    }

    [Fact]
    public async Task ImageToTensor_FourArg_AppliesMeanStdNormalize()
    {
        // 1×1 RGB=(128, 64, 200) with mean=[0.5,0.5,0.5], std=[0.5,0.5,0.5] —
        // symmetric normalize, easy mental math.
        using SKBitmap bmp = SolidBitmap(1, 1, 128, 64, 200);

        float[] result = await InvokeAsync(
            ValueRef.FromImage(bmp),
            Int32Array(1, 1),
            Float32Array(0.5f, 0.5f, 0.5f),
            Float32Array(0.5f, 0.5f, 0.5f));

        Assert.Equal(3, result.Length);
        Assert.Equal((128f / 255f - 0.5f) / 0.5f, result[0], 5);
        Assert.Equal(( 64f / 255f - 0.5f) / 0.5f, result[1], 5);
        Assert.Equal((200f / 255f - 0.5f) / 0.5f, result[2], 5);
    }

    [Fact]
    public async Task ImageToTensor_ComposesWithImagenetValues()
    {
        // 1×1 grey RGB=(127, 127, 127) with hand-typed ImageNet mean/std.
        // Cross-checks the math you'd get by combining image_to_tensor_chw with
        // imagenet_mean() / imagenet_std() at the SQL surface.
        using SKBitmap bmp = SolidBitmap(1, 1, 127, 127, 127);

        float[] result = await InvokeAsync(
            ValueRef.FromImage(bmp),
            Int32Array(1, 1),
            Float32Array(0.485f, 0.456f, 0.406f),
            Float32Array(0.229f, 0.224f, 0.225f));

        float p = 127f / 255f;
        Assert.Equal((p - 0.485f) / 0.229f, result[0], 5);
        Assert.Equal((p - 0.456f) / 0.224f, result[1], 5);
        Assert.Equal((p - 0.406f) / 0.225f, result[2], 5);
    }

    [Fact]
    public async Task ImageToTensor_ResizesToTargetDimensions()
    {
        // 8×8 source → 224×224 target. Output must be 3 × 224 × 224 floats.
        using SKBitmap bmp = SolidBitmap(8, 8, 200, 200, 200);

        float[] result = await InvokeAsync(ValueRef.FromImage(bmp), Int32Array(224, 224));

        Assert.Equal(3 * 224 * 224, result.Length);
        float expected = 200f / 255f;
        // Sample a few points across the channels.
        Assert.Equal(expected, result[0],          5);
        Assert.Equal(expected, result[224 * 224],  5);
        Assert.Equal(expected, result[result.Length - 1], 5);
    }

    [Fact]
    public async Task ImageToTensor_ZeroStdElement_Throws()
    {
        using SKBitmap bmp = SolidBitmap(1, 1, 50, 50, 50);

        await Assert.ThrowsAsync<FunctionArgumentException>(() => InvokeAsync(
            ValueRef.FromImage(bmp),
            Int32Array(1, 1),
            Float32Array(0f, 0f, 0f),
            Float32Array(1f, 0f, 1f)));
    }

    [Fact]
    public async Task ImageToTensor_WrongTargetSizeArity_Throws()
    {
        using SKBitmap bmp = SolidBitmap(1, 1, 0, 0, 0);

        await Assert.ThrowsAsync<FunctionArgumentException>(() => InvokeAsync(
            ValueRef.FromImage(bmp),
            Int32Array(1, 1, 1)));
    }

    [Fact]
    public async Task ImageToTensor_NullImage_ReturnsNullArray()
    {
        ImageToTensorChwFunction fn = new();
        ValueRef result = await fn.ExecuteAsync(
            new ReadOnlyMemory<ValueRef>([ValueRef.Null(DataKind.Image), Int32Array(1, 1)]),
            CreateEvaluationFrame(),
            CancellationToken.None);

        Assert.True(result.IsArray);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Float32, result.Kind);
    }

    [Fact]
    public void Metadata_ExposesNameCategoryAndDescription()
    {
        Assert.Equal("image_to_tensor_chw", ImageToTensorChwFunction.Name);
        Assert.Equal(DatumIngest.Manifest.FunctionCategory.Image, ImageToTensorChwFunction.Category);
        Assert.False(string.IsNullOrWhiteSpace(ImageToTensorChwFunction.Description));
    }
}
