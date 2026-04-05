using System.Buffers.Binary;
using System.Numerics;

using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Model.Spatial;

using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Spatial;

/// <summary>
/// <c>point_cloud_from_depth_pinhole_with_confidence(color Image, depth Float32[], confidence Float32[], fov_deg Float32, min_confidence Float32) → PointCloud</c>.
/// Pinhole sibling of <see cref="PointCloudFromDepthOrthographicWithConfidenceFunction"/>.
/// Same confidence-gated emission, geometrically-correct unprojection math.
/// Output is always unorganized (skipped pixels break the grid).
/// </summary>
public sealed class PointCloudFromDepthPinholeWithConfidenceFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "point_cloud_from_depth_pinhole_with_confidence";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Pinhole depth unprojection that gates pixel emission on per-pixel "
        + "confidence — same shape as point_cloud_from_depth_orthographic_with_confidence "
        + "but geometrically correct (planes look like planes). Pair with metric "
        + "depth for accurate scale.";

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
        FunctionMetadata.Validate<PointCloudFromDepthPinholeWithConfidenceFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull || args[3].IsNull || args[4].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.PointCloud));
        }

        if (!args[3].TryToFloat(out float fovDeg) || !(fovDeg > 0f && fovDeg < 180f))
        {
            throw new FunctionArgumentException(
                Name, $"fov_deg must be in (0, 180); got {args[3].Kind}.");
        }
        if (!args[4].TryToFloat(out float minConfidence))
        {
            throw new FunctionArgumentException(
                Name, $"min_confidence of kind {args[4].Kind} could not be widened to Float32.");
        }

        SKBitmap colorSrc = args[0].AsImage();
        int width = colorSrc.Width;
        int height = colorSrc.Height;
        if (width <= 0 || height <= 0)
        {
            throw new FunctionArgumentException(
                Name, $"color image has non-positive dimensions ({width}×{height}).");
        }

        ReadOnlySpan<float> depthMeters = ReadShapedFloat2D(args[1], frame, "depth", height, width);
        ReadOnlySpan<float> confidence = ReadShapedFloat2D(args[2], frame, "confidence", height, width);

        SKImageInfo rgbaInfo = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap colorRgba = new(rgbaInfo);
        if (!colorSrc.CopyTo(colorRgba, SKColorType.Rgba8888))
        {
            throw new FunctionArgumentException(
                Name, $"failed to convert color image to RGBA8888 (source: {colorSrc.ColorType}).");
        }

        // Derive focal from FOV (vertical) so the math matches the FOV-based
        // pinhole constructor.
        float fovRad = fovDeg * MathF.PI / 180f;
        float focalPx = (height / 2f) / MathF.Tan(fovRad / 2f);
        float cx = width / 2f;
        float cy = height / 2f;

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

                    float forward = depthMeters[rowBase + u];
                    if (!(forward > 0f) || float.IsNaN(forward) || float.IsInfinity(forward)) continue;

                    // Pinhole math: (X, Y) scale with depth.
                    float xCv = (u + 0.5f - cx) * forward / focalPx;
                    float yCv = (v + 0.5f - cy) * forward / focalPx;
                    float zCv = forward;

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
            Width: 0,
            Height: 0);
        outHeader.Write(outSpan[..PointCloudHeader.SizeBytes]);

        return new ValueTask<ValueRef>(ValueRef.FromPointCloud(outBlob));
    }

    private static ReadOnlySpan<float> ReadShapedFloat2D(
        ValueRef arg, EvaluationFrame frame, string paramName, int expectedH, int expectedW)
    {
        DataValue value = arg.ToDataValue(frame.Source);
        if (!value.IsMultiDim)
        {
            throw new FunctionArgumentException(
                Name,
                $"{paramName} must be a shape-aware Float32 array (2-D or batched); got flat 1-D.");
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
                $"{paramName} array must be 2-D (h, w) or batched (..., h, w); got shape "
                + $"[{string.Join(", ", shape.ToArray())}].");
        }

        if (dh != expectedH || dw != expectedW)
        {
            throw new FunctionArgumentException(
                Name,
                $"{paramName} dimensions ({dh}×{dw}) don't match color image "
                + $"({expectedH}×{expectedW}). Call array_resize_2d({paramName}, "
                + "image_height(color), image_width(color)) first.");
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
