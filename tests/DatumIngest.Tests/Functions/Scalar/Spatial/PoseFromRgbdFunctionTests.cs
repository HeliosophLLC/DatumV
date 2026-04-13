using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Spatial;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Pooling;

using SkiaSharp;

namespace DatumIngest.Tests.Functions.Scalar.Spatial;

/// <summary>
/// Tests for <see cref="PoseFromRgbdFunction"/>. Verifies metadata, null
/// handling, and end-to-end pose recovery on a synthetic RGB-D pair where
/// the ground-truth pose is known (textured "wall" at fixed depth viewed
/// from two cameras separated by a known X translation).
/// </summary>
public sealed class PoseFromRgbdFunctionTests : ServiceTestBase
{
    [Fact]
    public void Metadata_Exposes()
    {
        Assert.Equal("pose_from_rgbd", PoseFromRgbdFunction.Name);
        Assert.Equal(FunctionCategory.Spatial, PoseFromRgbdFunction.Category);
        Assert.False(string.IsNullOrWhiteSpace(PoseFromRgbdFunction.Description));
    }

    [Fact]
    public void Validate_AcceptsExpectedSignature_ReturnsFloat32()
    {
        PoseFromRgbdFunction fn = new();
        DataKind kind = fn.ValidateArguments(
            [DataKind.Image, DataKind.Float32, DataKind.Image, DataKind.Float32, DataKind.Float32]);
        Assert.Equal(DataKind.Float32, kind);
    }

    [Fact]
    public async Task Execute_NullPrevImage_ReturnsNullArray()
    {
        PoseFromRgbdFunction fn = new();
        const int w = 16;
        const int h = 16;
        ValueRef depth = MakeConstantDepth(w, h, 1.5f);
        ValueRef intrinsics = MakeIntrinsics(fx: 500f, fy: 500f, cx: w / 2f, cy: h / 2f);

        using SKBitmap img = MakeSolidColor(w, h, new SKColor(128, 128, 128, 255));
        ValueRef result = await fn.ExecuteAsync(
            new ValueRef[]
            {
                ValueRef.Null(DataKind.Image),
                depth,
                ValueRef.FromImage(img),
                depth,
                intrinsics,
            },
            CreateEvaluationFrame(),
            default);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Float32, result.Kind);
    }

    [Fact]
    public async Task Execute_KnownXTranslation_RecoversPose()
    {
        // ─── Ground-truth setup ────────────────────────────────────────
        // Textured "wall" at constant depth Z viewed from two cameras
        // separated purely along +X by groundTruthTx metres. The world is
        // a planar wall in front of the camera, so for any feature at
        // world point (X, Y, Z), the pixel shift between the two cameras
        // is exact and integer-aligned by construction:
        //
        //     dxPixels = groundTruthTx * fx / Z
        //
        // Pose maps curr-frame points to prev-frame points → identity
        // rotation, translation (groundTruthTx, 0, 0) in CV frame, same
        // in GL frame (X is preserved by the CV→GL flip).
        const int w = 256;
        const int h = 192;
        const float fx = 500f;
        const float fy = 500f;
        const float cx = w / 2f;
        const float cy = h / 2f;
        const float Z = 1.5f;
        const float groundTruthTx = 0.06f;
        const int dxPixels = (int)(groundTruthTx * fx / Z);  // 20
        Assert.Equal(20, dxPixels);                          // sanity — must be integer for the shift below to be exact

        // Build a "wide" texture and crop the two camera views out of it.
        // prev's view starts at column `margin`; curr's view starts at
        // `margin + dxPixels` — i.e. curr sees the world texture shifted
        // dxPixels to the LEFT relative to prev. That's the appearance
        // of the camera moving +X: a world feature at pattern column F
        // appears at u_prev = F − margin in prev, but at the smaller
        // u_curr = F − margin − dxPixels in curr (content shifts left
        // when the observer moves right).
        const int margin = 64;
        int patternWidth = w + margin * 2;
        using SKBitmap pattern = MakeRandomTexture(patternWidth, h, seed: 42);
        using SKBitmap prevImg = CropFrom(pattern, srcX: margin,            srcY: 0, w: w, h: h);
        using SKBitmap currImg = CropFrom(pattern, srcX: margin + dxPixels, srcY: 0, w: w, h: h);

        ValueRef depth = MakeConstantDepth(w, h, Z);
        ValueRef intrinsics = MakeIntrinsics(fx, fy, cx, cy);

        // ─── Execute ──────────────────────────────────────────────────
        PoseFromRgbdFunction fn = new();
        ValueRef result = await fn.ExecuteAsync(
            new ValueRef[]
            {
                ValueRef.FromImage(prevImg),
                depth,
                ValueRef.FromImage(currImg),
                depth,
                intrinsics,
            },
            CreateEvaluationFrame(),
            default);

        // ─── Assertions ───────────────────────────────────────────────
        Assert.False(result.IsNull);
        Assert.Equal(DataKind.Float32, result.Kind);
        Assert.True(result.IsArray);

        EvaluationFrame f = CreateEvaluationFrame();
        ReadOnlySpan<float> pose = result.ToDataValue(f.Source).AsArraySpan<float>(f.Source, f.SidecarRegistry);
        Assert.Equal(16, pose.Length);

        // Row-major 4×4: [R | t; 0 0 0 1].
        // Identity rotation expected — diagonal close to 1, off-diagonal close to 0.
        // Translation (pose[3], pose[7], pose[11]) close to (groundTruthTx, 0, 0).
        const float rotTol = 0.05f;
        const float transTol = 0.015f;  // 1.5 cm
        Assert.Equal(1f, pose[0],  rotTol);
        Assert.Equal(1f, pose[5],  rotTol);
        Assert.Equal(1f, pose[10], rotTol);
        Assert.Equal(0f, pose[1],  rotTol);
        Assert.Equal(0f, pose[2],  rotTol);
        Assert.Equal(0f, pose[4],  rotTol);
        Assert.Equal(0f, pose[6],  rotTol);
        Assert.Equal(0f, pose[8],  rotTol);
        Assert.Equal(0f, pose[9],  rotTol);

        Assert.Equal(groundTruthTx, pose[3],  transTol);
        Assert.Equal(0f,            pose[7],  transTol);
        Assert.Equal(0f,            pose[11], transTol);

        // Homogeneous bottom row.
        Assert.Equal(0f, pose[12]);
        Assert.Equal(0f, pose[13]);
        Assert.Equal(0f, pose[14]);
        Assert.Equal(1f, pose[15]);
    }

    [Fact]
    public async Task Execute_HugeDepthValues_HomogeneousBottomRowSurvives()
    {
        // Regression probe: real-world bug report showed pose matrices
        // with garbage in the bottom row (positions 12-15) instead of
        // [0, 0, 0, 1], when the depth model was emitting catastrophic
        // values. Even though Kabsch produces garbage R/t from garbage
        // 3D points (depth in the 1e+15 range → unprojected positions
        // astronomically large → SVD over huge covariance), the
        // hardcoded bottom row should still come through verbatim. This
        // pins that contract: whatever weirdness happens in R, t, the
        // bottom row of the returned 16-element Float32 is exactly
        // (0, 0, 0, 1). If this test ever fails, the float[] literal
        // construction at the end of ExecuteAsync has either been
        // mis-edited or there's a serialization issue downstream of
        // the function return.
        const int w = 256;
        const int h = 192;
        const float fx = 500f;
        const float fy = 500f;
        const float cx = w / 2f;
        const float cy = h / 2f;
        const float Z = 1.5e+15f;  // depth in the 1e+15 metres range
        const int dxPixels = 20;

        const int margin = 64;
        int patternWidth = w + margin * 2;
        using SKBitmap pattern = MakeRandomTexture(patternWidth, h, seed: 99);
        using SKBitmap prevImg = CropFrom(pattern, srcX: margin,            srcY: 0, w: w, h: h);
        using SKBitmap currImg = CropFrom(pattern, srcX: margin + dxPixels, srcY: 0, w: w, h: h);

        ValueRef depth = MakeConstantDepth(w, h, Z);
        ValueRef intrinsics = MakeIntrinsics(fx, fy, cx, cy);

        PoseFromRgbdFunction fn = new();
        ValueRef result;
        try
        {
            result = await fn.ExecuteAsync(
                new ValueRef[]
                {
                    ValueRef.FromImage(prevImg),
                    depth,
                    ValueRef.FromImage(currImg),
                    depth,
                    intrinsics,
                },
                CreateEvaluationFrame(),
                default);
        }
        catch (FunctionArgumentException)
        {
            // Garbage inputs can legitimately make RANSAC fail to find
            // a consensus — that throws. Either outcome (throw or
            // produce a 16-element array with valid bottom row) is
            // acceptable; the inadmissible outcome is producing an
            // array whose bottom row is not [0, 0, 0, 1].
            return;
        }

        Assert.False(result.IsNull);
        EvaluationFrame f = CreateEvaluationFrame();
        ReadOnlySpan<float> pose = result.ToDataValue(f.Source).AsArraySpan<float>(f.Source, f.SidecarRegistry);
        Assert.Equal(16, pose.Length);
        Assert.Equal(0f, pose[12]);
        Assert.Equal(0f, pose[13]);
        Assert.Equal(0f, pose[14]);
        Assert.Equal(1f, pose[15]);
    }

    // ─────────────────────── Helpers ───────────────────────

    private static SKBitmap MakeSolidColor(int width, int height, SKColor color)
    {
        SKBitmap bmp = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bmp.SetPixel(x, y, color);
            }
        }
        return bmp;
    }

    /// <summary>
    /// Procedurally-generated texture seeded for reproducibility. ORB
    /// needs corners; pure random per-pixel noise produces enough
    /// local-extrema corners across a 256-wide window to detect ~1000
    /// features — well above the function's MinFeaturesAfterDepthFilter
    /// threshold.
    /// </summary>
    private static SKBitmap MakeRandomTexture(int width, int height, int seed)
    {
        Random rng = new(seed);
        SKBitmap bmp = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte r = (byte)rng.Next(256);
                byte g = (byte)rng.Next(256);
                byte b = (byte)rng.Next(256);
                bmp.SetPixel(x, y, new SKColor(r, g, b, 255));
            }
        }
        return bmp;
    }

    private static SKBitmap CropFrom(SKBitmap src, int srcX, int srcY, int w, int h)
    {
        SKBitmap dst = new(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                dst.SetPixel(x, y, src.GetPixel(srcX + x, srcY + y));
            }
        }
        return dst;
    }

    private static ValueRef MakeConstantDepth(int width, int height, float metres)
    {
        float[] values = new float[width * height];
        Array.Fill(values, metres);
        return ValueRef.FromPrimitiveMultiDimArray(values, [height, width], DataKind.Float32);
    }

    private static ValueRef MakeIntrinsics(float fx, float fy, float cx, float cy)
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
