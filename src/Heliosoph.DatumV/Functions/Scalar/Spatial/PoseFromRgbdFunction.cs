using System.Runtime.InteropServices;

using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

using OpenCvSharp;
using OpenCvSharp.Features2D;

using SkiaSharp;

namespace Heliosoph.DatumV.Functions.Scalar.Spatial;

/// <summary>
/// <c>pose_from_rgbd(prev_image Image, prev_depth Float32[], curr_image Image,
/// curr_depth Float32[], intrinsics Float32[]) → Float32[]</c>. Recovers the
/// rigid 4×4 transform that brings <c>curr_image</c>'s camera frame into
/// <c>prev_image</c>'s frame using ORB feature matching + 3D-3D
/// Kabsch–Umeyama with RANSAC outlier rejection.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Output frame.</strong> The 16-element Float32 array is a 4×4
/// row-major matrix in the <see cref="Heliosoph.DatumV.Model.Spatial.PointCloudCoordinateFrame.CameraOpenGl"/>
/// frame — the same frame the depth-to-PointCloud constructors emit. The
/// returned pose composes directly: <c>pc_transform(curr_cloud, pose)</c>
/// lands curr_cloud's points in prev_cloud's coordinate system, ready for
/// fusion via <c>pc_fuse</c>.
/// </para>
/// <para>
/// <strong>Why RGB-D, not essential matrix.</strong> Monocular two-view
/// geometry (SIFT/ORB + RANSAC + essential matrix decomposition) recovers
/// rotation and a unit-length translation — translation magnitude is lost.
/// With per-frame metric depth available, lifting matched 2D features to
/// 3D and solving a 3D-3D rigid alignment gives metric translation directly
/// and is more robust (depth disambiguates ambiguous matches).
/// </para>
/// <para>
/// <strong>Intrinsics layout.</strong> Identical convention to
/// <c>point_cloud_from_depth_orthographic_intrinsics</c>: row-major 3×3 K
/// matrix flattened to 9 Float32 values. Only positions
/// <c>K[0]=fx, K[2]=cx, K[4]=fy, K[5]=cy</c> are read; the last 9 elements
/// are used when callers pass batched ONNX intrinsics shapes like
/// <c>(1, 1, 3, 3)</c>. Pair this function with the pinhole cloud
/// constructor — the geometry models match.
/// </para>
/// <para>
/// <strong>Depth layout.</strong> Each depth argument is a shape-aware
/// 2-D <c>Float32[h, w]</c> in metres, matching the depth dimensions of
/// the corresponding color image. Non-positive / NaN / Inf values are
/// treated as missing and skip the feature point.
/// </para>
/// <para>
/// <strong>Failure modes.</strong> Throws <see cref="FunctionArgumentException"/>
/// when (a) intrinsics, depth shape, or image dimensions disagree;
/// (b) fewer than 12 ORB matches survive the lift-to-3D filter (too few
/// features with valid depth in both frames); (c) RANSAC fails to find
/// a consensus set of ≥ 12 inliers (frames too different / depth too
/// noisy). The exception message names the failure so the caller can
/// react in SQL or skip the frame.
/// </para>
/// </remarks>
public sealed class PoseFromRgbdFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "pose_from_rgbd";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Spatial;

    /// <inheritdoc />
    public static string Description =>
        "Recovers a 4x4 rigid pose (row-major Float32[16], CameraOpenGl frame) "
        + "that maps curr_image's frame into prev_image's frame. Uses ORB "
        + "features + descriptor matching + 3D-3D Kabsch with RANSAC: each "
        + "matched pixel is lifted to 3D via the pinhole intrinsics and its "
        + "metric depth. Pass the result to pc_transform to align consecutive "
        + "RGB-D frames before pc_fuse. Intrinsics: 9-element row-major 3x3 "
        + "K matrix (same convention as point_cloud_from_depth_orthographic_"
        + "intrinsics). Depth: 2-D Float32 array in metres matching image "
        + "dimensions; non-positive / NaN / Inf entries skip that pixel. "
        + "Throws if fewer than 12 matched features have valid depth in both "
        + "frames or if RANSAC finds no consensus.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("prev_image", DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("prev_depth", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("curr_image", DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("curr_depth", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("intrinsics", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PoseFromRgbdFunction>(argumentKinds);

    // Tunables. These are picked to be reasonable defaults for indoor
    // RGB-D video at ~30 FPS — feature counts well above the minimum
    // inlier requirement, threshold scaled to the scene's depth range
    // so the function works across both close-up (tabletop) and large
    // (room-scale) scenes without re-tuning. If callers need to control
    // these, future overloads can accept a `params` struct.
    private const int OrbMaxFeatures = 1500;
    private const float LoweRatio = 0.75f;
    private const int RansacIterations = 500;
    private const float RansacInlierRelative = 0.01f;   // 1% of median depth
    private const float RansacInlierMinAbs = 0.03f;     // floor: 3 cm
    private const int MinFeaturesAfterDepthFilter = 12;
    private const int MinInliers = 12;

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef prevImgArg = args[0];
        ValueRef prevDepthArg = args[1];
        ValueRef currImgArg = args[2];
        ValueRef currDepthArg = args[3];
        ValueRef intrinsicsArg = args[4];

        if (prevImgArg.IsNull || prevDepthArg.IsNull
            || currImgArg.IsNull || currDepthArg.IsNull
            || intrinsicsArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.Float32));
        }

        // ─── Intrinsics ─────────────────────────────────────────────────
        DataValue intrinsicsValue = intrinsicsArg.ToDataValue(frame.Source);
        ReadOnlySpan<float> K = intrinsicsValue.AsArraySpan<float>(frame.Source, frame.SidecarRegistry);
        if (K.Length < 9)
        {
            throw new FunctionArgumentException(
                Name,
                $"intrinsics must contain at least 9 Float32 values (3x3 K matrix); got {K.Length}.");
        }
        int kBase = K.Length - 9;
        float fx = K[kBase + 0];
        float fy = K[kBase + 4];
        float cx = K[kBase + 2];
        float cy = K[kBase + 5];
        if (!(fx > 0f) || !(fy > 0f) || !float.IsFinite(fx) || !float.IsFinite(fy)
            || !float.IsFinite(cx) || !float.IsFinite(cy))
        {
            throw new FunctionArgumentException(
                Name,
                $"intrinsics has non-positive or non-finite focal/principal-point: "
                + $"fx={fx}, fy={fy}, cx={cx}, cy={cy}.");
        }

        // ─── Images ─────────────────────────────────────────────────────
        SKBitmap prevSrc = prevImgArg.AsImage();
        SKBitmap currSrc = currImgArg.AsImage();
        int width = prevSrc.Width;
        int height = prevSrc.Height;
        if (width <= 0 || height <= 0)
        {
            throw new FunctionArgumentException(
                Name,
                $"prev_image has non-positive dimensions ({width}x{height}).");
        }
        if (currSrc.Width != width || currSrc.Height != height)
        {
            throw new FunctionArgumentException(
                Name,
                $"prev_image and curr_image dimensions must match: "
                + $"prev={width}x{height}, curr={currSrc.Width}x{currSrc.Height}.");
        }

        // ─── Depth maps ─────────────────────────────────────────────────
        // Materialize into managed float[] we own. The arena-backed
        // ReadOnlySpan returned by AsArraySpan is only safe to hold for
        // the duration of the call; ORB+matcher work between read and
        // sample can trigger GC/arena recycling that invalidates the
        // span (manifests as AccessViolationException in SampleDepth).
        float[] prevDepth = ReadShapedDepth(prevDepthArg, frame, width, height, "prev_depth");
        float[] currDepth = ReadShapedDepth(currDepthArg, frame, width, height, "curr_depth");

        cancellationToken.ThrowIfCancellationRequested();

        // ─── ORB detect + compute on both frames ────────────────────────
        // Allocations: each grayscale conversion is held only for the
        // detect-and-compute call; descriptors are uint8 byte matrices
        // sized [N × 32] (BRIEF-256 in ORB).
        using Mat prevGray = ToGrayMat(prevSrc);
        using Mat currGray = ToGrayMat(currSrc);
        using ORB orb = ORB.Create(nFeatures: OrbMaxFeatures);
        using Mat prevDescs = new();
        using Mat currDescs = new();
        orb.DetectAndCompute(prevGray, null, out KeyPoint[] prevKps, prevDescs);
        orb.DetectAndCompute(currGray, null, out KeyPoint[] currKps, currDescs);

        if (prevKps.Length < MinFeaturesAfterDepthFilter
            || currKps.Length < MinFeaturesAfterDepthFilter
            || prevDescs.Empty() || currDescs.Empty())
        {
            throw new FunctionArgumentException(
                Name,
                $"too few ORB features detected (prev={prevKps.Length}, curr={currKps.Length}); "
                + $"need at least {MinFeaturesAfterDepthFilter} in each frame. Frames may be "
                + "too blurry, low-texture, or low-resolution.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // ─── Match + Lowe's ratio test ──────────────────────────────────
        // knnMatch with k=2 + ratio test rejects features whose second-best
        // match is nearly as good as the best — those are usually ambiguous
        // (repetitive texture, low-contrast patches) and produce inliers
        // that pull RANSAC off the true pose.
        using BFMatcher matcher = new(NormTypes.Hamming, crossCheck: false);
        DMatch[][] knn = matcher.KnnMatch(currDescs, prevDescs, k: 2);

        List<(int prevIdx, int currIdx)> goodMatches = new(knn.Length);
        for (int i = 0; i < knn.Length; i++)
        {
            DMatch[] pair = knn[i];
            if (pair.Length < 2) continue;
            if (pair[0].Distance < LoweRatio * pair[1].Distance)
            {
                goodMatches.Add((prevIdx: pair[0].TrainIdx, currIdx: pair[0].QueryIdx));
            }
        }

        if (goodMatches.Count < MinFeaturesAfterDepthFilter)
        {
            throw new FunctionArgumentException(
                Name,
                $"too few descriptor matches survived Lowe's ratio test ({goodMatches.Count}); "
                + $"need at least {MinFeaturesAfterDepthFilter}. Frames may have too little visual "
                + "overlap or too much appearance change between them.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // ─── Lift matched 2D points to 3D in OpenGL frame ───────────────
        // Same CV → GL flip the PointCloud constructors apply
        // (y_gl = -y_cv, z_gl = -z_cv), so the resulting pose is in the
        // cloud's native frame and can be passed straight to pc_transform.
        List<(double px, double py, double pz, double cxx, double cyy, double czz)> pairs = new(goodMatches.Count);
        foreach ((int prevIdx, int currIdx) in goodMatches)
        {
            KeyPoint pk = prevKps[prevIdx];
            KeyPoint ck = currKps[currIdx];

            float pz = SampleDepthNearest(prevDepth, width, height, pk.Pt.X, pk.Pt.Y);
            float cz = SampleDepthNearest(currDepth, width, height, ck.Pt.X, ck.Pt.Y);
            if (!(pz > 0f && cz > 0f) || !float.IsFinite(pz) || !float.IsFinite(cz))
            {
                continue;
            }

            // CV: X right, Y down, Z forward. Pinhole unprojection.
            double pxCv = (pk.Pt.X - cx) * pz / fx;
            double pyCv = (pk.Pt.Y - cy) * pz / fy;
            double pzCv = pz;
            double cxCv = (ck.Pt.X - cx) * cz / fx;
            double cyCv = (ck.Pt.Y - cy) * cz / fy;
            double czCv = cz;

            // GL: X right, Y up, -Z forward.
            pairs.Add((
                px:  pxCv,
                py: -pyCv,
                pz: -pzCv,
                cxx:  cxCv,
                cyy: -cyCv,
                czz: -czCv));
        }

        if (pairs.Count < MinFeaturesAfterDepthFilter)
        {
            throw new FunctionArgumentException(
                Name,
                $"only {pairs.Count} matched features had valid metric depth in both frames; "
                + $"need at least {MinFeaturesAfterDepthFilter}. Check that the depth maps "
                + "cover the scene at the matched feature locations (no large holes / zeros).");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // ─── RANSAC over 3D-3D rigid alignment ──────────────────────────
        double medianDepth = MedianForwardDepth(pairs);
        double inlierThreshold = System.Math.Max(RansacInlierMinAbs, medianDepth * RansacInlierRelative);
        double inlierThresholdSq = inlierThreshold * inlierThreshold;

        Random rng = new(0xC0FFEE);   // deterministic seed; reproducible pose for a given input
        int bestInlierCount = 0;
        bool[] bestInlierMask = new bool[pairs.Count];
        bool[] candidateMask = new bool[pairs.Count];

        for (int iter = 0; iter < RansacIterations; iter++)
        {
            if ((iter & 31) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            // Pick 3 distinct random pairs. 3 is the minimum for a unique
            // rigid 3D fit (a non-degenerate triangle defines R + t fully).
            int i0 = rng.Next(pairs.Count);
            int i1, i2;
            do { i1 = rng.Next(pairs.Count); } while (i1 == i0);
            do { i2 = rng.Next(pairs.Count); } while (i2 == i0 || i2 == i1);

            if (!TryKabschTriple(pairs, i0, i1, i2, out double[] candR, out double[] candT))
            {
                continue;
            }

            int count = CountInliers(pairs, candR, candT, inlierThresholdSq, candidateMask);
            if (count > bestInlierCount)
            {
                bestInlierCount = count;
                Array.Copy(candidateMask, bestInlierMask, candidateMask.Length);
            }
        }

        if (bestInlierCount < MinInliers)
        {
            throw new FunctionArgumentException(
                Name,
                $"RANSAC failed: best consensus set had {bestInlierCount} inliers, need {MinInliers}. "
                + $"Inlier threshold was {inlierThreshold:F4} m (median depth {medianDepth:F3} m). "
                + "Frames may share too little geometry or depth may be too noisy.");
        }

        // ─── Refine on all inliers ──────────────────────────────────────
        if (!TryKabschAll(pairs, bestInlierMask, out double[] R, out double[] t))
        {
            throw new FunctionArgumentException(
                Name,
                $"Kabsch refinement on {bestInlierCount} inliers produced a degenerate matrix "
                + "(SVD did not converge). This is rare — inputs may be co-linear in 3D.");
        }

        // ─── Pack to row-major 4×4 Float32 ──────────────────────────────
        float[] pose =
        [
            (float)R[0], (float)R[1], (float)R[2], (float)t[0],
            (float)R[3], (float)R[4], (float)R[5], (float)t[1],
            (float)R[6], (float)R[7], (float)R[8], (float)t[2],
            0f,          0f,          0f,          1f,
        ];

        return new ValueTask<ValueRef>(ValueRef.FromPrimitiveArray(pose, DataKind.Float32));
    }

    // ─────────────────────── Helpers ───────────────────────

    private static float[] ReadShapedDepth(
        ValueRef depthArg, EvaluationFrame frame, int width, int height, string paramName)
    {
        DataValue value = depthArg.ToDataValue(frame.Source);
        ReadOnlySpan<int> shape = value.GetShape(frame.Source, frame.SidecarRegistry);
        int dh, dw;
        if (shape.Length >= 2 && AllLeadingOnes(shape))
        {
            dh = shape[^2];
            dw = shape[^1];
        }
        else
        {
            throw new FunctionArgumentException(
                Name,
                $"{paramName} must be 2-D (h, w) or have only 1-dims before the trailing "
                + $"(h, w); got shape [{string.Join(", ", shape.ToArray())}].");
        }
        if (dh != height || dw != width)
        {
            throw new FunctionArgumentException(
                Name,
                $"{paramName} dimensions ({dh}x{dw}) don't match image ({height}x{width}).");
        }
        return value.AsArraySpan<float>(frame.Source, frame.SidecarRegistry).ToArray();
    }

    private static bool AllLeadingOnes(ReadOnlySpan<int> shape)
    {
        for (int i = 0; i < shape.Length - 2; i++)
        {
            if (shape[i] != 1) return false;
        }
        return true;
    }

    /// <summary>
    /// Converts an <see cref="SKBitmap"/> to an 8-bit grayscale OpenCV
    /// <see cref="Mat"/>. The Mat owns its data — the source bitmap can
    /// be disposed independently.
    /// </summary>
    private static Mat ToGrayMat(SKBitmap src)
    {
        int w = src.Width;
        int h = src.Height;
        SKImageInfo rgbaInfo = new(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap rgba = new(rgbaInfo);
        if (!src.CopyTo(rgba, SKColorType.Rgba8888))
        {
            throw new FunctionArgumentException(
                Name,
                $"failed to convert image to RGBA8888 (source colour type: {src.ColorType}).");
        }

        // Create a header-only Mat over the SKBitmap pixels, then CvtColor
        // into a freshly-allocated grayscale Mat. The output Mat owns its
        // buffer, so it survives `rgba.Dispose()`.
        Mat result = new();
        using (Mat rgbaMat = Mat.FromPixelData(h, w, MatType.CV_8UC4, rgba.GetPixels()))
        {
            Cv2.CvtColor(rgbaMat, result, ColorConversionCodes.RGBA2GRAY);
        }
        return result;
    }

    /// <summary>
    /// Nearest-neighbor depth sample at a sub-pixel feature coordinate.
    /// Nearest-neighbor (not bilinear) is the safer choice for depth maps
    /// — bilinear blends across discontinuities at object boundaries,
    /// producing depths that exist nowhere in the scene and pulling
    /// matched 3D points off-surface.
    /// </summary>
    private static float SampleDepthNearest(float[] depth, int width, int height, float u, float v)
    {
        int iu = (int)MathF.Round(u);
        int iv = (int)MathF.Round(v);
        if (iu < 0 || iu >= width || iv < 0 || iv >= height)
        {
            return 0f;
        }
        return depth[iv * width + iu];
    }

    private static double MedianForwardDepth(
        List<(double px, double py, double pz, double cxx, double cyy, double czz)> pairs)
    {
        // Forward distance in GL frame is -z (negate to recover positive metres).
        double[] zs = new double[pairs.Count];
        for (int i = 0; i < pairs.Count; i++) zs[i] = -pairs[i].pz;
        Array.Sort(zs);
        return zs[zs.Length / 2];
    }

    /// <summary>
    /// Counts how many curr→prev pairs project within the inlier threshold
    /// when transformed by (R, t). Fills <paramref name="mask"/> with the
    /// per-pair inlier flags so the caller can adopt the best set without
    /// recomputing.
    /// </summary>
    private static int CountInliers(
        List<(double px, double py, double pz, double cxx, double cyy, double czz)> pairs,
        double[] R, double[] t, double thresholdSq, bool[] mask)
    {
        int count = 0;
        for (int i = 0; i < pairs.Count; i++)
        {
            var p = pairs[i];
            double tx = R[0] * p.cxx + R[1] * p.cyy + R[2] * p.czz + t[0];
            double ty = R[3] * p.cxx + R[4] * p.cyy + R[5] * p.czz + t[1];
            double tz = R[6] * p.cxx + R[7] * p.cyy + R[8] * p.czz + t[2];

            double dx = tx - p.px;
            double dy = ty - p.py;
            double dz = tz - p.pz;
            bool ok = dx * dx + dy * dy + dz * dz <= thresholdSq;
            mask[i] = ok;
            if (ok) count++;
        }
        return count;
    }

    /// <summary>
    /// Closed-form rigid alignment of two 3D triples. Returns false when the
    /// 3 source or 3 target points are co-linear / coincident — the SVD's
    /// reflection-flip step needs three independent dimensions to yield a
    /// proper rotation. Caller treats false as "skip this RANSAC sample."
    /// </summary>
    private static bool TryKabschTriple(
        List<(double px, double py, double pz, double cxx, double cyy, double czz)> pairs,
        int i0, int i1, int i2,
        out double[] R, out double[] t)
    {
        Span<int> idx = stackalloc int[3] { i0, i1, i2 };
        return TryKabsch(pairs, idx, out R, out t);
    }

    private static bool TryKabschAll(
        List<(double px, double py, double pz, double cxx, double cyy, double czz)> pairs,
        bool[] inlierMask,
        out double[] R, out double[] t)
    {
        int n = 0;
        for (int i = 0; i < inlierMask.Length; i++) if (inlierMask[i]) n++;
        int[] idx = new int[n];
        int k = 0;
        for (int i = 0; i < inlierMask.Length; i++) if (inlierMask[i]) idx[k++] = i;
        return TryKabsch(pairs, idx, out R, out t);
    }

    /// <summary>
    /// Kabsch–Umeyama for rigid 3D alignment (no scale). Computes R, t such
    /// that R * curr_i + t ≈ prev_i for the indexed subset. Uses OpenCV
    /// SVD on the 3×3 covariance matrix and applies the determinant-fix
    /// step so the returned R is a proper rotation (det = +1) rather than
    /// an improper rotation that includes a reflection.
    /// </summary>
    private static bool TryKabsch(
        List<(double px, double py, double pz, double cxx, double cyy, double czz)> pairs,
        ReadOnlySpan<int> idx,
        out double[] R, out double[] t)
    {
        R = new double[9];
        t = new double[3];
        int n = idx.Length;
        if (n < 3) return false;

        double pxm = 0, pym = 0, pzm = 0, cxm = 0, cym = 0, czm = 0;
        for (int j = 0; j < n; j++)
        {
            var p = pairs[idx[j]];
            pxm += p.px;  pym += p.py;  pzm += p.pz;
            cxm += p.cxx; cym += p.cyy; czm += p.czz;
        }
        double inv = 1.0 / n;
        pxm *= inv; pym *= inv; pzm *= inv;
        cxm *= inv; cym *= inv; czm *= inv;

        // H = sum (centered_curr) * (centered_prev)^T — 3x3 covariance.
        double[] H = new double[9];
        for (int j = 0; j < n; j++)
        {
            var p = pairs[idx[j]];
            double qx = p.cxx - cxm, qy = p.cyy - cym, qz = p.czz - czm;
            double dx = p.px  - pxm, dy = p.py  - pym, dz = p.pz  - pzm;
            H[0] += qx * dx; H[1] += qx * dy; H[2] += qx * dz;
            H[3] += qy * dx; H[4] += qy * dy; H[5] += qy * dz;
            H[6] += qz * dx; H[7] += qz * dy; H[8] += qz * dz;
        }

        // SVD via OpenCV. Pre-allocate outputs as CV_64F 3x3 so SVDecomp
        // reuses the buffers rather than allocating fresh Mats whose
        // element type we'd have to discover at runtime — defending
        // against version-specific behaviour where new Mat() outputs
        // come back as CV_32F and silently corrupt the post-SVD math.
        GCHandle hH = GCHandle.Alloc(H, GCHandleType.Pinned);
        try
        {
            using Mat Hm = Mat.FromPixelData(3, 3, MatType.CV_64F, hH.AddrOfPinnedObject());
            using Mat W = new(3, 1, MatType.CV_64F);
            using Mat U = new(3, 3, MatType.CV_64F);
            using Mat Vt = new(3, 3, MatType.CV_64F);
            try
            {
                Cv2.SVDecomp(Hm, W, U, Vt, SVD.Flags.FullUV);
            }
            catch
            {
                return false;
            }

            // H = U * diag(W) * Vt → R = V * diag(1, 1, det(V*U^T)) * U^T.
            // det(V*U^T) is either +1 (proper) or -1 (improper / reflection).
            // The diag(1,1,-1) flip corrects an improper rotation back to SO(3).
            double[] Uarr = new double[9];
            double[] Vtarr = new double[9];
            if (!TryCopyMat3x3(U, Uarr) || !TryCopyMat3x3(Vt, Vtarr))
            {
                return false;
            }

            // V = Vt^T; compute det(V * U^T) = det(Vt^T * U^T) = det((U*Vt)^T) = det(U*Vt).
            double[] UVt = Mul3x3(Uarr, Vtarr);
            double det = Det3x3(UVt);
            double s = det >= 0 ? 1.0 : -1.0;

            // R = V * diag(1, 1, s) * U^T. Compute step by step.
            // M = diag(1, 1, s) * U^T → scale row 2 of U^T by s.
            double[] Ut = Transpose3x3(Uarr);
            Ut[6] *= s; Ut[7] *= s; Ut[8] *= s;
            double[] V = Transpose3x3(Vtarr);
            double[] Rmat = Mul3x3(V, Ut);
            Array.Copy(Rmat, R, 9);
        }
        finally
        {
            hH.Free();
        }

        // t = mean_prev - R * mean_curr
        t[0] = pxm - (R[0] * cxm + R[1] * cym + R[2] * czm);
        t[1] = pym - (R[3] * cxm + R[4] * cym + R[5] * czm);
        t[2] = pzm - (R[6] * cxm + R[7] * cym + R[8] * czm);

        // Sanity checks. A proper rotation has |R_ij| <= 1 strictly and
        // det(R) = +1. Reject anything wildly outside those bounds — a
        // garbage R typically signals SVD pathology on degenerate input
        // (co-linear / co-planar inlier triple) or a type-mismatch read
        // that escaped the TryCopyMat3x3 guard. Bound is generous (10×
        // the legal range) to allow numerical slop without admitting
        // matrices whose entries would later overflow float32 when fed
        // through pose_compose.
        for (int i = 0; i < 9; i++)
        {
            if (!double.IsFinite(R[i]) || System.Math.Abs(R[i]) > 10.0) return false;
        }
        for (int i = 0; i < 3; i++)
        {
            if (!double.IsFinite(t[i])) return false;
        }
        double detR = Det3x3(R);
        if (!(detR > 0.5 && detR < 1.5)) return false;

        return true;
    }

    /// <summary>
    /// Copies a 3×3 Mat into a managed double[9]. Returns false (rather
    /// than reading garbage) if the Mat isn't 3×3 of a supported numeric
    /// type. Defensive against the version-specific OpenCvSharp behaviour
    /// where <c>new Mat()</c> output buffers came back as CV_32F despite
    /// CV_64F input — <c>Mat.At&lt;double&gt;</c> on a CV_32F buffer reads
    /// 8 bytes per cell from a 4-byte-per-cell stride and produces
    /// nonsense values in the 1e+30 range.
    /// </summary>
    private static bool TryCopyMat3x3(Mat src, double[] dst)
    {
        if (src.Rows != 3 || src.Cols != 3) return false;
        MatType t = src.Type();
        if (t == MatType.CV_64F)
        {
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    dst[r * 3 + c] = src.At<double>(r, c);
                }
            }
            return true;
        }
        if (t == MatType.CV_32F)
        {
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    dst[r * 3 + c] = src.At<float>(r, c);
                }
            }
            return true;
        }
        return false;
    }

    private static double[] Mul3x3(double[] a, double[] b)
    {
        double[] r = new double[9];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                double s = 0;
                for (int k = 0; k < 3; k++) s += a[i * 3 + k] * b[k * 3 + j];
                r[i * 3 + j] = s;
            }
        }
        return r;
    }

    private static double[] Transpose3x3(double[] a) =>
    [
        a[0], a[3], a[6],
        a[1], a[4], a[7],
        a[2], a[5], a[8],
    ];

    private static double Det3x3(double[] m) =>
        m[0] * (m[4] * m[8] - m[5] * m[7])
      - m[1] * (m[3] * m[8] - m[5] * m[6])
      + m[2] * (m[3] * m[7] - m[4] * m[6]);
}
