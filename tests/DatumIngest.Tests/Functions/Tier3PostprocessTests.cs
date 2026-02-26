using System.Numerics;

using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Activation;
using DatumIngest.Functions.Scalar.Image;
using DatumIngest.Functions.Scalar.Vector;
using DatumIngest.Model;
using DatumIngest.Pooling;

using SkiaSharp;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Direct-invocation tests for the Tier 3 postprocess helpers: softmax,
/// sigmoid, argmax, topk, l2_normalize, cosine_similarity, nms,
/// tensor_to_image, mask_to_polygon.
/// </summary>
/// <remarks>
/// Invokes each function class directly with a synthetic
/// <see cref="EvaluationFrame"/> — sidesteps the catalog arena-lifecycle
/// plumbing required to feed array values through a SQL plan and keeps
/// the assertions focused on the math.
/// </remarks>
public sealed class Tier3PostprocessTests : ServiceTestBase
{
    private EvaluationFrame MakeFrame()
    {
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        return new EvaluationFrame(Row.Empty, arena, arena, types: new TypeRegistry());
    }

    private static ValueRef F32(params float[] values) =>
        ValueRef.FromPrimitiveArray(values, DataKind.Float32);

    private static async Task<ValueRef> InvokeAsync(IScalarFunction fn, params ValueRef[] args)
        => await fn.ExecuteAsync(args.AsMemory(),
            new EvaluationFrame(Row.Empty, new Arena(), new Arena(), types: new TypeRegistry()),
            CancellationToken.None);

    private static float[] AsFloatArr(ValueRef v) => (float[])v.Materialized!;
    private static int[] AsIntArr(ValueRef v) => (int[])v.Materialized!;

    // ─── softmax ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Softmax_KnownInput_MatchesCanonicalValues()
    {
        ValueRef result = await InvokeAsync(new SoftmaxFunction(), F32(1f, 2f, 3f));
        float[] probs = AsFloatArr(result);

        // softmax([1, 2, 3]) ≈ [0.0900, 0.2447, 0.6652]
        Assert.Equal(3, probs.Length);
        Assert.Equal(0.09003f, probs[0], 4);
        Assert.Equal(0.24473f, probs[1], 4);
        Assert.Equal(0.66524f, probs[2], 4);

        float sum = probs[0] + probs[1] + probs[2];
        Assert.Equal(1f, sum, 5);
    }

    [Fact]
    public async Task Softmax_LargeMagnitudes_DoesntOverflow()
    {
        // exp(1000) overflows to +inf without the max-subtract trick.
        ValueRef result = await InvokeAsync(new SoftmaxFunction(), F32(1000f, 1001f, 1002f));
        float[] probs = AsFloatArr(result);
        Assert.All(probs, p => Assert.True(!float.IsNaN(p) && !float.IsInfinity(p)));
        float sum = probs[0] + probs[1] + probs[2];
        Assert.Equal(1f, sum, 5);
    }

    // ─── sigmoid ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sigmoid_KnownInputs_MatchExpectedValues()
    {
        ValueRef result = await InvokeAsync(new SigmoidFunction(), F32(0f, 100f, -100f));
        float[] out_ = AsFloatArr(result);
        Assert.Equal(0.5f, out_[0], 5);
        Assert.Equal(1f, out_[1], 5);
        Assert.Equal(0f, out_[2], 5);
    }

    // ─── argmax ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Argmax_TieBreaksToLowestIndex()
    {
        ValueRef result = await InvokeAsync(new ArgmaxFunction(), F32(0.5f, 0.5f, 0.5f));
        Assert.Equal(0, result.ToInt32());
    }

    [Fact]
    public async Task Argmax_PicksTheLargest()
    {
        ValueRef result = await InvokeAsync(new ArgmaxFunction(), F32(0.1f, 0.7f, 0.2f));
        Assert.Equal(1, result.ToInt32());
    }

    [Fact]
    public async Task Argmax_EmptyInput_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(() => InvokeAsync(new ArgmaxFunction(), F32()));
    }

    // ─── topk ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Topk_ReturnsIndicesDescending()
    {
        // values: [0.1, 0.5, 0.3, 0.9, 0.2] — top-3 indices by value
        // descending are [3 (0.9), 1 (0.5), 2 (0.3)].
        ValueRef result = await InvokeAsync(new TopkFunction(),
            F32(0.1f, 0.5f, 0.3f, 0.9f, 0.2f),
            ValueRef.FromInt32(3));
        Assert.Equal([3, 1, 2], AsIntArr(result));
    }

    [Fact]
    public async Task Topk_KExceedingLength_ReturnsAllIndices()
    {
        ValueRef result = await InvokeAsync(new TopkFunction(),
            F32(1f, 2f, 3f),
            ValueRef.FromInt32(10));
        int[] indices = AsIntArr(result);
        Assert.Equal(3, indices.Length);
    }

    // ─── l2_normalize ────────────────────────────────────────────────────────

    [Fact]
    public async Task L2Normalize_3_4_Normalizes_To_0p6_0p8()
    {
        ValueRef result = await InvokeAsync(new L2NormalizeFunction(), F32(3f, 4f));
        float[] u = AsFloatArr(result);
        Assert.Equal(0.6f, u[0], 5);
        Assert.Equal(0.8f, u[1], 5);
    }

    [Fact]
    public async Task L2Normalize_AllZero_ReturnsAllZero()
    {
        ValueRef result = await InvokeAsync(new L2NormalizeFunction(), F32(0f, 0f, 0f));
        Assert.All(AsFloatArr(result), v => Assert.Equal(0f, v));
    }

    // ─── cosine_similarity ───────────────────────────────────────────────────

    [Fact]
    public async Task CosineSimilarity_Identical_Returns1()
    {
        ValueRef result = await InvokeAsync(new CosineSimilarityFunction(),
            F32(1f, 2f, 3f), F32(1f, 2f, 3f));
        Assert.Equal(1f, (float)result.ToDouble(), 5);
    }

    [Fact]
    public async Task CosineSimilarity_Orthogonal_Returns0()
    {
        ValueRef result = await InvokeAsync(new CosineSimilarityFunction(),
            F32(1f, 0f, 0f), F32(0f, 1f, 0f));
        Assert.Equal(0f, (float)result.ToDouble(), 5);
    }

    [Fact]
    public async Task CosineSimilarity_Opposite_ReturnsMinus1()
    {
        ValueRef result = await InvokeAsync(new CosineSimilarityFunction(),
            F32(1f, 0f), F32(-1f, 0f));
        Assert.Equal(-1f, (float)result.ToDouble(), 5);
    }

    [Fact]
    public async Task CosineSimilarity_LengthMismatch_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(() =>
            InvokeAsync(new CosineSimilarityFunction(), F32(1f, 2f), F32(1f, 2f, 3f)));
    }

    // ─── nms ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Nms_TwoOverlappingBoxes_KeepsHigherScoreOnly()
    {
        // Two boxes that overlap heavily.
        ValueRef boxes = F32(
            10f, 10f, 50f, 50f,   // box 0: (10,10)-(50,50)
            15f, 15f, 55f, 55f);  // box 1: (15,15)-(55,55) — large overlap with box 0
        ValueRef scores = F32(0.9f, 0.7f);
        ValueRef result = await InvokeAsync(new NmsFunction(), boxes, scores, ValueRef.FromFloat32(0.5f));

        int[] kept = AsIntArr(result);
        Assert.Equal([0], kept);
    }

    [Fact]
    public async Task Nms_NonOverlappingBoxes_KeepsAll()
    {
        ValueRef boxes = F32(
            0f, 0f, 10f, 10f,        // box 0
            20f, 20f, 30f, 30f,      // box 1
            40f, 40f, 50f, 50f);     // box 2
        ValueRef scores = F32(0.9f, 0.8f, 0.7f);
        ValueRef result = await InvokeAsync(new NmsFunction(), boxes, scores, ValueRef.FromFloat32(0.5f));

        int[] kept = AsIntArr(result);
        // All boxes survive (no overlap); order is score-descending = original index order.
        Assert.Equal([0, 1, 2], kept);
    }

    [Fact]
    public async Task Nms_InvalidIouThreshold_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(() => InvokeAsync(
            new NmsFunction(),
            F32(0f, 0f, 10f, 10f),
            F32(0.9f),
            ValueRef.FromFloat32(1.5f)));
    }

    // ─── tensor_to_image ─────────────────────────────────────────────────────

    [Fact]
    public async Task TensorToImage_ReconstructsSolidColor()
    {
        // 1×1 RGB(128, 64, 200) → tensor at /255 → reconstruct via 3-arg form.
        float r = 128f / 255f, g = 64f / 255f, b = 200f / 255f;
        ValueRef result = await InvokeAsync(new TensorToImageFunction(),
            F32(r, g, b),
            ValueRef.FromInt32(1), ValueRef.FromInt32(1));

        Assert.Equal(DataKind.Image, result.Kind);
        using SKBitmap bmp = result.AsImage();
        SKColor px = bmp.GetPixel(0, 0);
        Assert.Equal(128, px.Red);
        Assert.Equal(64, px.Green);
        Assert.Equal(200, px.Blue);
    }

    [Fact]
    public async Task TensorToImage_RoundTripsImageToTensor()
    {
        // Build a 2×2 solid image, push through image_to_tensor, then back
        // through tensor_to_image. Pixels should round-trip within 1 byte.
        using SKBitmap solid = new(2, 2, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using (SKCanvas canvas = new(solid)) canvas.Clear(new SKColor(100, 200, 50));

        ValueRef forward = await InvokeAsync(new ImageToTensorFunction(),
            ValueRef.FromImage(solid),
            ValueRef.FromPrimitiveArray(new[] { 2, 2 }, DataKind.Int32));
        float[] tensor = AsFloatArr(forward);

        ValueRef back = await InvokeAsync(new TensorToImageFunction(),
            ValueRef.FromPrimitiveArray(tensor, DataKind.Float32),
            ValueRef.FromInt32(2), ValueRef.FromInt32(2));

        using SKBitmap reconstructed = back.AsImage();
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                SKColor px = reconstructed.GetPixel(x, y);
                Assert.InRange(px.Red - 100, -1, 1);
                Assert.InRange(px.Green - 200, -1, 1);
                Assert.InRange(px.Blue - 50, -1, 1);
            }
        }
    }

    [Fact]
    public async Task TensorToImage_WrongLength_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(() => InvokeAsync(
            new TensorToImageFunction(),
            F32(1f, 2f, 3f, 4f),                            // length 4 ≠ 3*2*2=12
            ValueRef.FromInt32(2), ValueRef.FromInt32(2)));
    }

    // ─── mask_to_polygon ─────────────────────────────────────────────────────

    [Fact]
    public async Task MaskToPolygon_CenteredSquare_ReturnsRoughlyRectangularContour()
    {
        // 5×5 mask with a 3×3 centred filled square.
        // 0 0 0 0 0
        // 0 1 1 1 0
        // 0 1 1 1 0
        // 0 1 1 1 0
        // 0 0 0 0 0
        float[] mask = new float[25];
        for (int y = 1; y <= 3; y++)
            for (int x = 1; x <= 3; x++)
                mask[y * 5 + x] = 1f;

        ValueRef result = await InvokeAsync(new MaskToPolygonFunction(),
            ValueRef.FromPrimitiveArray(mask, DataKind.Float32),
            ValueRef.FromInt32(5), ValueRef.FromInt32(5),
            ValueRef.FromFloat32(0.5f));

        Vector2[] points = (Vector2[])result.Materialized!;
        Assert.True(points.Length >= 3, $"Expected at least 3 vertices for a closed polygon, got {points.Length}.");

        // All vertices should land near the iso-contour at half-pixel inset
        // from the 3×3 box's corners — i.e. roughly between x∈[0.5, 3.5]
        // and y∈[0.5, 3.5].
        foreach (Vector2 p in points)
        {
            Assert.InRange(p.X, 0.4f, 3.6f);
            Assert.InRange(p.Y, 0.4f, 3.6f);
        }

        // Vertices should span the full box range — extremes near 0.5 and 3.5.
        float minX = points[0].X, maxX = points[0].X;
        float minY = points[0].Y, maxY = points[0].Y;
        foreach (Vector2 p in points)
        {
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }
        Assert.InRange(minX, 0.4f, 0.6f);
        Assert.InRange(maxX, 3.4f, 3.6f);
        Assert.InRange(minY, 0.4f, 0.6f);
        Assert.InRange(maxY, 3.4f, 3.6f);
    }

    [Fact]
    public async Task MaskToPolygon_EmptyMask_ReturnsEmptyArray()
    {
        float[] mask = new float[25];   // all zeros
        ValueRef result = await InvokeAsync(new MaskToPolygonFunction(),
            ValueRef.FromPrimitiveArray(mask, DataKind.Float32),
            ValueRef.FromInt32(5), ValueRef.FromInt32(5),
            ValueRef.FromFloat32(0.5f));

        Vector2[] points = (Vector2[])result.Materialized!;
        Assert.Empty(points);
    }

    [Fact]
    public async Task MaskToPolygon_WrongLength_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(() => InvokeAsync(
            new MaskToPolygonFunction(),
            F32(1f, 2f, 3f, 4f),                            // length 4 ≠ 5*5
            ValueRef.FromInt32(5), ValueRef.FromInt32(5),
            ValueRef.FromFloat32(0.5f)));
    }
}
