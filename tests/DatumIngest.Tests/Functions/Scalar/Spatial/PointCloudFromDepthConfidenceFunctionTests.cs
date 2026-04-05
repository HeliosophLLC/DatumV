using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Spatial;
using DatumIngest.Model;
using DatumIngest.Model.Spatial;
using DatumIngest.Pooling;

using SkiaSharp;

namespace DatumIngest.Tests.Functions.Scalar.Spatial;

public sealed class PointCloudFromDepthConfidenceFunctionTests : ServiceTestBase
{
    [Fact]
    public async Task HighConfidenceThreshold_DropsLowConfidencePixels()
    {
        EvaluationFrame f = MakeFrame();
        ValueRef color = BuildColorImage(4, 4);
        ValueRef depth = BuildShapedFloatArray(4, 4, fillValue: 1.0f, f);
        // Half the pixels at confidence=0.2, half at confidence=0.8.
        ValueRef conf = BuildShapedConfidenceArray(4, 4, lowHalf: 0.2f, highHalf: 0.8f, f);

        ValueRef result = await new PointCloudFromDepthOrthographicWithConfidenceFunction().ExecuteAsync(
            new[]
            {
                color, depth, conf,
                ValueRef.FromFloat32(60f),
                ValueRef.FromFloat32(0.5f),    // threshold drops the 0.2 half
            },
            f, default);

        PointCloudHeader header = PointCloudHeader.Read(result.AsPointCloud());
        // 16 pixels, 8 kept (confidence 0.8 ≥ 0.5).
        Assert.Equal(8u, header.PointCount);
        Assert.False(header.IsOrganized);
    }

    [Fact]
    public async Task ZeroThreshold_KeepsEverything()
    {
        EvaluationFrame f = MakeFrame();
        ValueRef color = BuildColorImage(3, 3);
        ValueRef depth = BuildShapedFloatArray(3, 3, fillValue: 2.5f, f);
        ValueRef conf = BuildShapedConfidenceArray(3, 3, lowHalf: 0.1f, highHalf: 0.9f, f);

        ValueRef result = await new PointCloudFromDepthOrthographicWithConfidenceFunction().ExecuteAsync(
            new[]
            {
                color, depth, conf,
                ValueRef.FromFloat32(60f),
                ValueRef.FromFloat32(0.0f),
            },
            f, default);

        PointCloudHeader header = PointCloudHeader.Read(result.AsPointCloud());
        Assert.Equal(9u, header.PointCount);
    }

    [Fact]
    public async Task ThresholdAboveAll_ReturnsEmpty()
    {
        EvaluationFrame f = MakeFrame();
        ValueRef color = BuildColorImage(2, 2);
        ValueRef depth = BuildShapedFloatArray(2, 2, fillValue: 1.0f, f);
        ValueRef conf = BuildShapedConfidenceArray(2, 2, lowHalf: 0.2f, highHalf: 0.5f, f);

        ValueRef result = await new PointCloudFromDepthOrthographicWithConfidenceFunction().ExecuteAsync(
            new[]
            {
                color, depth, conf,
                ValueRef.FromFloat32(60f),
                ValueRef.FromFloat32(0.99f),
            },
            f, default);

        PointCloudHeader header = PointCloudHeader.Read(result.AsPointCloud());
        Assert.Equal(0u, header.PointCount);
    }

    [Fact]
    public async Task DepthAndConfidenceDimensionMismatch_Throws()
    {
        EvaluationFrame f = MakeFrame();
        ValueRef color = BuildColorImage(4, 4);
        ValueRef depth = BuildShapedFloatArray(4, 4, fillValue: 1.0f, f);
        ValueRef wrongConf = BuildShapedFloatArray(2, 2, fillValue: 1.0f, f);   // mismatched

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new PointCloudFromDepthOrthographicWithConfidenceFunction().ExecuteAsync(
                new[]
                {
                    color, depth, wrongConf,
                    ValueRef.FromFloat32(60f),
                    ValueRef.FromFloat32(0.5f),
                },
                f, default));
        Assert.Contains("confidence", ex.Message);
    }

    [Fact]
    public async Task NullInput_PropagatesNull()
    {
        EvaluationFrame f = MakeFrame();
        ValueRef color = BuildColorImage(2, 2);

        ValueRef result = await new PointCloudFromDepthOrthographicWithConfidenceFunction().ExecuteAsync(
            new[]
            {
                color,
                ValueRef.Null(DataKind.Float32),
                ValueRef.Null(DataKind.Float32),
                ValueRef.FromFloat32(60f),
                ValueRef.FromFloat32(0.5f),
            },
            f, default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.PointCloud, result.Kind);
    }

    private EvaluationFrame MakeFrame()
    {
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        return new EvaluationFrame(Row.Empty, arena, arena, new MemoryAccountant(), types: new TypeRegistry());
    }

    private static ValueRef BuildColorImage(int w, int h)
    {
        SKBitmap bmp = new(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                bmp.SetPixel(x, y, new SKColor(100, 150, 200, 255));
        return ValueRef.FromImage(bmp);
    }

    private static ValueRef BuildShapedFloatArray(int h, int w, float fillValue, EvaluationFrame frame)
    {
        float[] data = new float[h * w];
        Array.Fill(data, fillValue);
        return ValueRef.FromPrimitiveMultiDimArray(data, new[] { h, w }, DataKind.Float32);
    }

    private static ValueRef BuildShapedConfidenceArray(int h, int w, float lowHalf, float highHalf, EvaluationFrame frame)
    {
        float[] data = new float[h * w];
        int halfRows = h / 2;
        for (int y = 0; y < h; y++)
        {
            float v = y < halfRows ? highHalf : lowHalf;
            for (int x = 0; x < w; x++)
            {
                data[y * w + x] = v;
            }
        }
        return ValueRef.FromPrimitiveMultiDimArray(data, new[] { h, w }, DataKind.Float32);
    }
}
