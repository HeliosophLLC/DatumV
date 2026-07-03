using System.Buffers.Binary;

using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Spatial;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Model.Spatial;

using SkiaSharp;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Spatial;

public sealed class PointCloudFromDepthPinholeIntrinsicsConfidenceFunctionTests : ServiceTestBase
{
    [Fact]
    public async Task ConfidenceThreshold_GatesPixelEmission()
    {
        EvaluationFrame f = CreateEvaluationFrame();
        ValueRef color = BuildColorImage(4, 4);
        ValueRef depth = BuildShapedFloatArray(4, 4, fillValue: 1.0f);
        ValueRef conf = BuildShapedConfidenceArray(4, 4, lowHalf: 0.2f, highHalf: 0.8f);

        ValueRef result = await new PointCloudFromDepthPinholeIntrinsicsWithConfidenceFunction().ExecuteAsync(
            new[]
            {
                color, depth, conf,
                BuildK(fx: 4f, fy: 4f, cx: 2f, cy: 2f),
                ValueRef.FromFloat32(0.5f),
            },
            f, default);

        PointCloudHeader header = PointCloudHeader.Read(result.AsPointCloud());
        Assert.Equal(8u, header.PointCount);
        Assert.False(header.IsOrganized);
        Assert.Equal(PointCloudCoordinateFrame.CameraOpenGl, header.CoordinateFrame);
    }

    [Fact]
    public async Task UnprojectionMatchesIntrinsicsVariantMath()
    {
        // Single kept pixel at (u=3, v=1) of a 4x4 grid, depth 2, K with
        // distinct per-axis focals. Expected (CV): x = (3.5 − 2)·2/8 = 0.375,
        // y = (1.5 − 1)·2/4 = 0.25, z = 2 → GL (0.375, −0.25, −2).
        EvaluationFrame f = CreateEvaluationFrame();
        ValueRef color = BuildColorImage(4, 4);
        ValueRef depth = BuildShapedFloatArray(4, 4, fillValue: 2.0f);

        float[] confData = new float[16];
        confData[1 * 4 + 3] = 1.0f;   // only (u=3, v=1) survives
        ValueRef conf = ValueRef.FromPrimitiveMultiDimArray(confData, [4, 4], DataKind.Float32);

        ValueRef result = await new PointCloudFromDepthPinholeIntrinsicsWithConfidenceFunction().ExecuteAsync(
            new[]
            {
                color, depth, conf,
                BuildK(fx: 8f, fy: 4f, cx: 2f, cy: 1f),
                ValueRef.FromFloat32(0.5f),
            },
            f, default);

        byte[] blob = result.AsPointCloud();
        PointCloudHeader header = PointCloudHeader.Read(blob);
        Assert.Equal(1u, header.PointCount);

        ReadOnlySpan<byte> span = blob;
        int p0 = PointCloudHeader.SizeBytes;
        Assert.Equal(0.375f, BinaryPrimitives.ReadSingleLittleEndian(span.Slice(p0 + 0, 4)), 5);
        Assert.Equal(-0.25f, BinaryPrimitives.ReadSingleLittleEndian(span.Slice(p0 + 4, 4)), 5);
        Assert.Equal(-2.0f, BinaryPrimitives.ReadSingleLittleEndian(span.Slice(p0 + 8, 4)), 5);
    }

    [Fact]
    public async Task BatchedIntrinsicsShape_ReadsTrailingNine()
    {
        // (1, 1, 3, 3)-shaped K — the raw ONNX intrinsics layout — must work
        // without slicing: the trailing 9 elements are the matrix.
        EvaluationFrame f = CreateEvaluationFrame();
        ValueRef color = BuildColorImage(2, 2);
        ValueRef depth = BuildShapedFloatArray(2, 2, fillValue: 1.0f);
        ValueRef conf = BuildShapedFloatArray(2, 2, fillValue: 1.0f);

        float[] batchedK = [2f, 0f, 1f, 0f, 2f, 1f, 0f, 0f, 1f];
        ValueRef k = ValueRef.FromPrimitiveMultiDimArray(batchedK, [1, 1, 3, 3], DataKind.Float32);

        ValueRef result = await new PointCloudFromDepthPinholeIntrinsicsWithConfidenceFunction().ExecuteAsync(
            new[] { color, depth, conf, k, ValueRef.FromFloat32(0.5f) },
            f, default);

        PointCloudHeader header = PointCloudHeader.Read(result.AsPointCloud());
        Assert.Equal(4u, header.PointCount);
    }

    [Fact]
    public async Task NonPositiveFocal_Throws()
    {
        EvaluationFrame f = CreateEvaluationFrame();
        ValueRef color = BuildColorImage(2, 2);
        ValueRef depth = BuildShapedFloatArray(2, 2, fillValue: 1.0f);
        ValueRef conf = BuildShapedFloatArray(2, 2, fillValue: 1.0f);

        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new PointCloudFromDepthPinholeIntrinsicsWithConfidenceFunction().ExecuteAsync(
                new[]
                {
                    color, depth, conf,
                    BuildK(fx: 0f, fy: 2f, cx: 1f, cy: 1f),
                    ValueRef.FromFloat32(0.5f),
                },
                f, default));
    }

    [Fact]
    public async Task ConfidenceDimensionMismatch_Throws()
    {
        EvaluationFrame f = CreateEvaluationFrame();
        ValueRef color = BuildColorImage(4, 4);
        ValueRef depth = BuildShapedFloatArray(4, 4, fillValue: 1.0f);
        ValueRef wrongConf = BuildShapedFloatArray(2, 2, fillValue: 1.0f);

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new PointCloudFromDepthPinholeIntrinsicsWithConfidenceFunction().ExecuteAsync(
                new[]
                {
                    color, depth, wrongConf,
                    BuildK(fx: 4f, fy: 4f, cx: 2f, cy: 2f),
                    ValueRef.FromFloat32(0.5f),
                },
                f, default));
        Assert.Contains("confidence", ex.Message);
    }

    [Fact]
    public async Task NullInput_PropagatesNull()
    {
        EvaluationFrame f = CreateEvaluationFrame();
        ValueRef color = BuildColorImage(2, 2);

        ValueRef result = await new PointCloudFromDepthPinholeIntrinsicsWithConfidenceFunction().ExecuteAsync(
            new[]
            {
                color,
                ValueRef.Null(DataKind.Float32),
                ValueRef.Null(DataKind.Float32),
                BuildK(fx: 2f, fy: 2f, cx: 1f, cy: 1f),
                ValueRef.FromFloat32(0.5f),
            },
            f, default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.PointCloud, result.Kind);
    }

    private static ValueRef BuildK(float fx, float fy, float cx, float cy) =>
        ValueRef.FromPrimitiveArray([fx, 0f, cx, 0f, fy, cy, 0f, 0f, 1f], DataKind.Float32);

    private static ValueRef BuildColorImage(int w, int h)
    {
        SKBitmap bmp = new(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                bmp.SetPixel(x, y, new SKColor(100, 150, 200, 255));
        return ValueRef.FromImage(bmp);
    }

    private static ValueRef BuildShapedFloatArray(int h, int w, float fillValue)
    {
        float[] data = new float[h * w];
        Array.Fill(data, fillValue);
        return ValueRef.FromPrimitiveMultiDimArray(data, [h, w], DataKind.Float32);
    }

    private static ValueRef BuildShapedConfidenceArray(int h, int w, float lowHalf, float highHalf)
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
        return ValueRef.FromPrimitiveMultiDimArray(data, [h, w], DataKind.Float32);
    }
}
