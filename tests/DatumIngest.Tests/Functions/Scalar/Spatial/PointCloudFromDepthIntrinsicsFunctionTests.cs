using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Spatial;
using DatumIngest.Model;
using DatumIngest.Model.Spatial;

using SkiaSharp;

namespace DatumIngest.Tests.Functions.Scalar.Spatial;

public sealed class PointCloudFromDepthIntrinsicsFunctionTests : ServiceTestBase
{
    [Fact]
    public async Task ReturnsOrganizedCloud_MatchingImageDimensions()
    {
        ValueRef color = BuildColorImage(4, 4);
        ValueRef depth = BuildDepthImage(4, 4);
        ValueRef intrinsics = BuildIntrinsics(fx: 4f, fy: 4f, cx: 2f, cy: 2f);

        ValueRef result = await new PointCloudFromDepthOrthographicIntrinsicsFunction().ExecuteAsync(
            new[] { color, depth, intrinsics }, CreateEvaluationFrame(), default);

        PointCloudHeader header = PointCloudHeader.Read(result.AsPointCloud());
        Assert.Equal(16u, header.PointCount);
        Assert.Equal(4u, header.Width);
        Assert.Equal(4u, header.Height);
        Assert.True(header.HasColor);
        Assert.True(header.IsOrganized);
    }

    [Fact]
    public async Task IntrinsicsCenter_AppliedToXYPositions()
    {
        // 4x4 image, principal point at (2, 2) → image-center pixel (2, 2)
        // unprojects to xCv = (2.5 - 2.0) / 4 = 0.125; yCv = 0.125 → y in GL frame
        // is negated to -0.125. Just a smoke-check that we used cx/cy, not the
        // hardcoded width/2, height/2 of the FOV path.
        ValueRef color = BuildColorImage(4, 4);
        ValueRef depth = BuildDepthImage(4, 4);

        // Shift cx far left so the bbox shifts right (since x = (u - cx)/fx).
        ValueRef shiftedIntrinsics = BuildIntrinsics(fx: 4f, fy: 4f, cx: 0f, cy: 0f);
        ValueRef shiftedResult = await new PointCloudFromDepthOrthographicIntrinsicsFunction().ExecuteAsync(
            new[] { color, depth, shiftedIntrinsics }, CreateEvaluationFrame(), default);

        ValueRef centered = BuildIntrinsics(fx: 4f, fy: 4f, cx: 2f, cy: 2f);
        ValueRef centeredResult = await new PointCloudFromDepthOrthographicIntrinsicsFunction().ExecuteAsync(
            new[] { color, depth, centered }, CreateEvaluationFrame(), default);

        PointCloudHeader hShifted = PointCloudHeader.Read(shiftedResult.AsPointCloud());
        PointCloudHeader hCentered = PointCloudHeader.Read(centeredResult.AsPointCloud());

        // Shifted cx=0 → all pixels have positive x; centered cx=2 → x straddles 0.
        Assert.True(hShifted.BboxMin.X > hCentered.BboxMin.X,
            $"shifted bbox.min.X ({hShifted.BboxMin.X}) should exceed centered ({hCentered.BboxMin.X})");
    }

    [Fact]
    public async Task AcceptsBatchedIntrinsicsShape()
    {
        // 9-element K is fine; verify (1, 1, 3, 3) → 9 elements is also accepted
        // (trailing 9 used). The function strips leading batch dims by reading
        // the last 9 elements as the K matrix.
        ValueRef color = BuildColorImage(2, 2);
        ValueRef depth = BuildDepthImage(2, 2);

        float[] batchedK =
        [
            // Padding bytes simulating a different batched element — they should be ignored.
            999f, 999f, 999f, 999f, 999f, 999f, 999f, 999f, 999f,
            // Trailing 9 = the actual K matrix.
            4f, 0f, 1f,
            0f, 4f, 1f,
            0f, 0f, 1f,
        ];
        ValueRef intrinsics = ValueRef.FromPrimitiveArray(batchedK, DataKind.Float32);

        ValueRef result = await new PointCloudFromDepthOrthographicIntrinsicsFunction().ExecuteAsync(
            new[] { color, depth, intrinsics }, CreateEvaluationFrame(), default);

        PointCloudHeader header = PointCloudHeader.Read(result.AsPointCloud());
        Assert.Equal(4u, header.PointCount);
    }

    [Fact]
    public async Task IntrinsicsTooShort_Throws()
    {
        ValueRef color = BuildColorImage(2, 2);
        ValueRef depth = BuildDepthImage(2, 2);
        ValueRef shortK = ValueRef.FromPrimitiveArray(new float[] { 1, 0, 0 }, DataKind.Float32);

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new PointCloudFromDepthOrthographicIntrinsicsFunction().ExecuteAsync(
                new[] { color, depth, shortK }, CreateEvaluationFrame(), default));
        Assert.Contains("9", ex.Message);
    }

    [Fact]
    public async Task NonPositiveFocal_Throws()
    {
        ValueRef color = BuildColorImage(2, 2);
        ValueRef depth = BuildDepthImage(2, 2);
        ValueRef badK = BuildIntrinsics(fx: -1f, fy: 1f, cx: 1f, cy: 1f);

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new PointCloudFromDepthOrthographicIntrinsicsFunction().ExecuteAsync(
                new[] { color, depth, badK }, CreateEvaluationFrame(), default));
        Assert.Contains("focal", ex.Message);
    }

    [Fact]
    public async Task NullInput_PropagatesNull()
    {
        ValueRef color = BuildColorImage(2, 2);
        ValueRef intrinsics = BuildIntrinsics(fx: 1f, fy: 1f, cx: 1f, cy: 1f);

        ValueRef result = await new PointCloudFromDepthOrthographicIntrinsicsFunction().ExecuteAsync(
            new[] { color, ValueRef.Null(DataKind.Image), intrinsics }, CreateEvaluationFrame(), default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.PointCloud, result.Kind);
    }

    private static ValueRef BuildColorImage(int w, int h)
    {
        SKBitmap bmp = new(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                bmp.SetPixel(x, y, new SKColor(100, 100, 100, 255));
        return ValueRef.FromImage(bmp);
    }

    private static ValueRef BuildDepthImage(int w, int h)
    {
        SKBitmap bmp = new(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                bmp.SetPixel(x, y, new SKColor(128, 128, 128, 255));
        return ValueRef.FromImage(bmp);
    }

    private static ValueRef BuildIntrinsics(float fx, float fy, float cx, float cy)
    {
        float[] K =
        [
            fx, 0f, cx,
            0f, fy, cy,
            0f, 0f, 1f,
        ];
        return ValueRef.FromPrimitiveArray(K, DataKind.Float32);
    }
}
