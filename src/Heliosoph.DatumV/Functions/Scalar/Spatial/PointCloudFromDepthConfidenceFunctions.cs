using System.Buffers.Binary;
using System.Numerics;

using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Model.Spatial;

using SkiaSharp;

namespace Heliosoph.DatumV.Functions.Scalar.Spatial;

/// <summary>
/// <c>point_cloud_from_depth_orthographic_with_confidence(color Image, depth Float32[], confidence Float32[], fov_deg Float32, min_confidence Float32) → PointCloud</c>.
/// Orthographic unprojection that gates pixel emission on a per-pixel
/// confidence score. Pixels with <c>confidence &lt; min_confidence</c> are
/// dropped before they ever land in the cloud — no need to filter by depth
/// after the fact. Output is always unorganized (the skipped pixels break
/// the grid layout).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this primitive exists.</strong> Per-pixel depth confidence
/// is intrinsically scale-normalized — a threshold of 0.5 generalizes across
/// scenes in a way that absolute depth cutoffs (whose meaning depends on
/// each frame's predicted depth range) never can. Pair with
/// <c>da3_base_full</c>'s <c>confidence</c> output, or any
/// other depth model that emits a confidence map alongside depth.
/// </para>
/// <para>
/// <strong>Confidence layout.</strong> The confidence argument must be
/// shape-aware <c>(h, w)</c> (or rank-N with leading 1-dims squeezable) at
/// the same dimensions as the depth array AND the color image. Resize at
/// the call site if the model emits at native resolution and your depth is
/// already resized.
/// </para>
/// <para>
/// <strong>Threshold typical values.</strong> 0.5 is a reasonable starting
/// point for DA-v3-large (drops object edges + specular highlights +
/// featureless backgrounds). Raise to 0.7+ for aggressive cleanup; lower
/// to 0.3 if too much geometry vanishes.
/// </para>
/// </remarks>
public sealed class PointCloudFromDepthOrthographicWithConfidenceFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "point_cloud_from_depth_orthographic_with_confidence";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Orthographic depth unprojection that gates pixel emission on a per-pixel "
        + "confidence score — pixels with confidence < min_confidence are skipped. "
        + "Drops untrustworthy depth predictions (object edges, specular highlights, "
        + "featureless backgrounds) before they ever enter the cloud. Confidence "
        + "thresholds generalize across frames in a way that absolute depth cutoffs "
        + "don't. Output is always unorganized.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("color",          DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("depth",          DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("confidence",     DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("fov_deg",        DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("min_confidence", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.PointCloud)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PointCloudFromDepthOrthographicWithConfidenceFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef colorArg = args[0];
        ValueRef depthArg = args[1];
        ValueRef confArg = args[2];
        ValueRef fovArg = args[3];
        ValueRef thresholdArg = args[4];

        if (colorArg.IsNull || depthArg.IsNull || confArg.IsNull || fovArg.IsNull || thresholdArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.PointCloud));
        }

        if (!fovArg.TryToFloat(out float fovDeg) || !(fovDeg > 0f && fovDeg < 180f))
        {
            throw new FunctionArgumentException(
                Name, $"fov_deg must be in (0, 180); got {fovArg.Kind}.");
        }
        if (!thresholdArg.TryToFloat(out float minConfidence))
        {
            throw new FunctionArgumentException(
                Name, $"min_confidence of kind {thresholdArg.Kind} could not be widened to Float32.");
        }

        SKBitmap colorSrc = colorArg.AsImage();
        int width = colorSrc.Width;
        int height = colorSrc.Height;
        if (width <= 0 || height <= 0)
        {
            throw new FunctionArgumentException(
                Name, $"color image has non-positive dimensions ({width}×{height}).");
        }

        // Materialize both into managed arrays BEFORE any further arena work.
        // Two ToDataValue calls can each trigger arena growth, which would
        // invalidate any span obtained from the first call. ToArray copies into
        // GC-managed memory, immune to arena mutation. See the pinhole
        // confidence sibling for the discovery of this bug (AV on the second
        // span access).
        float[] depthMeters = ReadShapedFloat2D(
            depthArg, frame, "depth", height, width).ToArray();
        float[] confidence = ReadShapedFloat2D(
            confArg, frame, "confidence", height, width).ToArray();

        // Stabilize color byte order — Windows BGRA vs RGBA elsewhere.
        SKImageInfo rgbaInfo = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap colorRgba = new(rgbaInfo);
        if (!colorSrc.CopyTo(colorRgba, SKColorType.Rgba8888))
        {
            throw new FunctionArgumentException(
                Name, $"failed to convert color image to RGBA8888 (source: {colorSrc.ColorType}).");
        }

        // Pinhole / orthographic constants — orthographic uses focal only as
        // an X/Y scale factor, same convention as the FOV-based ortho path.
        float fovRad = fovDeg * MathF.PI / 180f;
        float focalPx = (height / 2f) / MathF.Tan(fovRad / 2f);
        float cx = width / 2f;
        float cy = height / 2f;

        // Two-pass: first count survivors so we can size the output blob
        // exactly, second pass writes them out. Sparing the realloc-and-trim
        // cycle on what's typically a 30–70% drop rate.
        int keepCount = 0;
        for (int v = 0; v < height; v++)
        {
            int rowBase = v * width;
            for (int u = 0; u < width; u++)
            {
                float c = confidence[rowBase + u];
                if (c >= minConfidence && !float.IsNaN(c)) keepCount++;
            }
        }

        int outStride = PointCloudHeader.PositionStrideBytes + PointCloudHeader.ColorStrideBytes;
        byte[] outBlob = new byte[PointCloudHeader.SizeBytes + (long)keepCount * outStride];
        Span<byte> outSpan = outBlob;
        Vector3 bboxMin = new(float.PositiveInfinity);
        Vector3 bboxMax = new(float.NegativeInfinity);

        nint colorPtr = colorRgba.GetPixels();
        int writeOffset = PointCloudHeader.SizeBytes;

        unsafe
        {
            byte* color = (byte*)colorPtr;
            for (int v = 0; v < height; v++)
            {
                int rowBase = v * width;
                int colorRowBase = rowBase * 4;
                for (int u = 0; u < width; u++)
                {
                    float c = confidence[rowBase + u];
                    if (!(c >= minConfidence) || float.IsNaN(c)) continue;

                    // Reject NaN / Inf / non-positive depth as background.
                    float forward = depthMeters[rowBase + u];
                    if (!(forward > 0f) || float.IsNaN(forward) || float.IsInfinity(forward))
                    {
                        continue;
                    }

                    float xCv = (u + 0.5f - cx) / focalPx;
                    float yCv = (v + 0.5f - cy) / focalPx;
                    float zCv = forward;

                    // CV → GL: +y down → +y up, +z forward → −z forward.
                    float x = xCv;
                    float y = -yCv;
                    float z = -zCv;

                    int colorOffset = colorRowBase + u * 4;
                    Span<byte> outSlot = outSpan.Slice(writeOffset, outStride);
                    BinaryPrimitives.WriteSingleLittleEndian(outSlot[0..4], x);
                    BinaryPrimitives.WriteSingleLittleEndian(outSlot[4..8], y);
                    BinaryPrimitives.WriteSingleLittleEndian(outSlot[8..12], z);
                    outSlot[12] = color[colorOffset + 0];
                    outSlot[13] = color[colorOffset + 1];
                    outSlot[14] = color[colorOffset + 2];
                    outSlot[15] = color[colorOffset + 3];

                    bboxMin = Vector3.Min(bboxMin, new Vector3(x, y, z));
                    bboxMax = Vector3.Max(bboxMax, new Vector3(x, y, z));
                    writeOffset += outStride;
                }
            }
        }

        // The two-pass count was over confidence-only; this can be slightly
        // higher than the actual write count if depth had its own NaN/Inf
        // skips. Trim if so.
        int actualWritten = (writeOffset - PointCloudHeader.SizeBytes) / outStride;
        if (actualWritten != keepCount)
        {
            byte[] trimmed = new byte[PointCloudHeader.SizeBytes + (long)actualWritten * outStride];
            outSpan[..writeOffset].CopyTo(trimmed);
            outBlob = trimmed;
            outSpan = outBlob;
            keepCount = actualWritten;
        }

        if (keepCount == 0)
        {
            bboxMin = Vector3.Zero;
            bboxMax = Vector3.Zero;
        }

        PointCloudHeader outHeader = new(
            Version: PointCloudHeader.CurrentVersion,
            Flags: PointCloudFlags.HasColor,
            CoordinateFrame: PointCloudCoordinateFrame.CameraOpenGl,
            PointCount: (uint)keepCount,
            BboxMin: bboxMin,
            BboxMax: bboxMax,
            Width: 0,    // unorganized — skipped pixels break the grid
            Height: 0);
        outHeader.Write(outSpan[..PointCloudHeader.SizeBytes]);

        return new ValueTask<ValueRef>(ValueRef.FromPointCloud(outBlob));
    }

    /// <summary>
    /// Reads a shape-aware Float32 array, validates it has trailing (h, w)
    /// dimensions matching the expected size with any leading 1-dims allowed
    /// (so DAv3's (1, 1, h, w) batched output works without forcing the
    /// caller to squeeze).
    /// </summary>
    private static ReadOnlySpan<float> ReadShapedFloat2D(
        ValueRef arg, EvaluationFrame frame, string paramName, int expectedH, int expectedW)
    {
        DataValue value = arg.ToDataValue(frame.Source);
        if (!value.IsMultiDim)
        {
            throw new FunctionArgumentException(
                Name,
                $"{paramName} must be a shape-aware Float32 array (2-D or batched (..., h, w)); "
                + "got a flat 1-D Float32[].");
        }

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
                $"{paramName} array must be 2-D (h, w) or batched (..., h, w) with leading 1-dims; "
                + $"got shape [{string.Join(", ", shape.ToArray())}].");
        }

        if (dh != expectedH || dw != expectedW)
        {
            throw new FunctionArgumentException(
                Name,
                $"{paramName} dimensions ({dh}×{dw}) don't match color image "
                + $"({expectedH}×{expectedW}). Call array_resize_2d({paramName}, "
                + "image_height(color), image_width(color)) first to align.");
        }

        return value.AsArraySpan<float>(frame.Source, frame.SidecarRegistry);
    }

    private static bool AllLeadingOnes(ReadOnlySpan<int> shape)
    {
        for (int i = 0; i < shape.Length - 2; i++)
        {
            if (shape[i] != 1) return false;
        }
        return true;
    }
}
