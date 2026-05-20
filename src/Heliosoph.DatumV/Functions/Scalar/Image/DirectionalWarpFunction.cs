using System.Runtime.InteropServices;

using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using SkiaSharp;

namespace Heliosoph.DatumV.Functions.Scalar.Image;

/// <summary>
/// <c>directional_warp(img Image, dx, dy, intensity) → Image</c>. Linear
/// directional shear along the 2D direction <c>(dx, dy)</c>. The direction
/// is normalised internally (so its length is ignored — only its angle
/// matters); <c>intensity</c> is the absolute pixel displacement applied at
/// the perpendicularly-furthest edge of the image.
/// </summary>
/// <remarks>
/// <para>
/// For each output pixel at signed perpendicular distance <c>s</c> from the
/// centre line orthogonal to <c>(dx, dy)</c>, the source is sampled at
/// <c>(out_x - n_dx · intensity · s/half_perp_extent, out_y - n_dy · intensity · s/half_perp_extent)</c>,
/// where <c>(n_dx, n_dy)</c> is the unit direction. Pixels on the centre
/// line stay put; opposite edges displace in opposite directions along the
/// direction vector. Bilinear sampling, edge clamping.
/// </para>
/// <para>
/// Designed for MNIST-style synthetic data augmentation: tilting a digit
/// "slightly in a 2D direction". E.g. <c>directional_warp(img, 1, 0, 2)</c>
/// on a 28×28 digit gives a 2-pixel horizontal lean. The image y-axis is
/// down (SkiaSharp convention).
/// </para>
/// </remarks>
public sealed class DirectionalWarpFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "directional_warp";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Linear directional shear along the 2D vector (dx, dy). Direction is normalised internally; "
        + "intensity is the absolute pixel displacement at the perpendicular edge. Pixels on the "
        + "centre line don't move; opposite edges displace in opposite directions. Bilinear sampling.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("image",     DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("dx",        DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("dy",        DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("intensity", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DirectionalWarpFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].IsNull)
            {
                return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
            }
        }

        float dx = args[1].ToFloat();
        float dy = args[2].ToFloat();
        float intensity = args[3].ToFloat();

        float dirLen = MathF.Sqrt(dx * dx + dy * dy);
        if (dirLen < 1e-6f)
        {
            throw new FunctionArgumentException(Name,
                "direction vector (dx, dy) is zero; cannot determine warp direction.");
        }
        float ndx = dx / dirLen;
        float ndy = dy / dirLen;
        // Perpendicular: rotate 90° CCW in image space.
        float perpx = -ndy;
        float perpy = ndx;

        SKBitmap source = args[0].AsImage();
        SKBitmap rgba = ImagePixelAccess.AsRgba8888(source, out SKBitmap? owned);
        try
        {
            int width = rgba.Width;
            int height = rgba.Height;
            float cx = (width - 1) / 2f;
            float cy = (height - 1) / 2f;

            // Half perpendicular extent = max projection of any corner onto the
            // perpendicular axis. Picks the farthest corner so opposite edges
            // displace by ±intensity along the direction.
            float halfExtent = MathF.Max(
                MathF.Abs(cx * perpx) + MathF.Abs(cy * perpy),
                1e-3f);

            ReadOnlySpan<byte> src = rgba.GetPixelSpan();
            byte[] outBytes = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float rx = x - cx;
                    float ry = y - cy;
                    float s = rx * perpx + ry * perpy;
                    float displacement = intensity * (s / halfExtent);
                    float srcX = x - ndx * displacement;
                    float srcY = y - ndy * displacement;
                    BilinearSampleClamped(src, width, height, srcX, srcY,
                                          outBytes, (y * width + x) * 4);
                }
            }

            SKBitmap result = new(new SKImageInfo(width, height, SKColorType.Rgba8888,
                                                  rgba.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Unpremul));
            Marshal.Copy(outBytes, 0, result.GetPixels(), outBytes.Length);
            return new ValueTask<ValueRef>(ValueRef.FromImage(result));
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private static void BilinearSampleClamped(
        ReadOnlySpan<byte> source, int width, int height,
        float fx, float fy, byte[] destination, int destOffset)
    {
        fx = System.Math.Clamp(fx, 0f, width - 1.001f);
        fy = System.Math.Clamp(fy, 0f, height - 1.001f);
        int x0 = (int)fx;
        int y0 = (int)fy;
        int x1 = System.Math.Min(x0 + 1, width - 1);
        int y1 = System.Math.Min(y0 + 1, height - 1);
        float u = fx - x0;
        float v = fy - y0;

        for (int c = 0; c < 4; c++)
        {
            float tl = source[(y0 * width + x0) * 4 + c];
            float tr = source[(y0 * width + x1) * 4 + c];
            float bl = source[(y1 * width + x0) * 4 + c];
            float br = source[(y1 * width + x1) * 4 + c];
            float top = tl + (tr - tl) * u;
            float bot = bl + (br - bl) * u;
            float val = top + (bot - top) * v;
            destination[destOffset + c] =
                val <= 0f ? (byte)0 : val >= 255f ? (byte)255 : (byte)val;
        }
    }
}
