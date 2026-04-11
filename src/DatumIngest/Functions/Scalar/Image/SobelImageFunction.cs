using System.Runtime.InteropServices;

using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Image;

/// <summary>
/// <c>sobel(img Image) → Image</c>. Sobel edge detector. Converts the input
/// to BT.601 grayscale, applies the 3×3 horizontal and vertical Sobel
/// kernels, and emits an RGBA8888 image whose RGB channels each carry the
/// per-pixel edge magnitude (clamped to 255). The 1-pixel border is set to
/// opaque black.
/// </summary>
public sealed class SobelImageFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "sobel";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Sobel edge detection. Produces a grayscale edge-magnitude image. "
        + "1-pixel border is opaque black.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("image", DataKindMatcher.Exact(DataKind.Image))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<SobelImageFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef imgArg = arguments.Span[0];
        if (imgArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
        }

        SKBitmap source = imgArg.AsImage();
        SKBitmap rgba = ImagePixelAccess.AsRgba8888(source, out SKBitmap? owned);
        try
        {
            int width = rgba.Width;
            int height = rgba.Height;
            ReadOnlySpan<byte> pixels = rgba.GetPixelSpan();

            float[] gray = new float[width * height];
            for (int i = 0; i < gray.Length; i++)
            {
                int o = i * 4;
                gray[i] = pixels[o] * ImagePixelAccess.Bt601RedWeight
                        + pixels[o + 1] * ImagePixelAccess.Bt601GreenWeight
                        + pixels[o + 2] * ImagePixelAccess.Bt601BlueWeight;
            }

            byte[] outBytes = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                int rowAbove = (y - 1) * width;
                int rowMid = y * width;
                int rowBelow = (y + 1) * width;
                for (int x = 0; x < width; x++)
                {
                    int idx = (rowMid + x) * 4;
                    byte mag;
                    if (y == 0 || y == height - 1 || x == 0 || x == width - 1)
                    {
                        mag = 0;
                    }
                    else
                    {
                        float gx = -gray[rowAbove + x - 1]
                                 +  gray[rowAbove + x + 1]
                                 - 2f * gray[rowMid + x - 1]
                                 + 2f * gray[rowMid + x + 1]
                                 -  gray[rowBelow + x - 1]
                                 +  gray[rowBelow + x + 1];
                        float gy = -gray[rowAbove + x - 1]
                                 - 2f * gray[rowAbove + x]
                                 -  gray[rowAbove + x + 1]
                                 +  gray[rowBelow + x - 1]
                                 + 2f * gray[rowBelow + x]
                                 +  gray[rowBelow + x + 1];
                        float m = MathF.Sqrt(gx * gx + gy * gy);
                        mag = m >= 255f ? (byte)255 : (byte)m;
                    }
                    outBytes[idx]     = mag;
                    outBytes[idx + 1] = mag;
                    outBytes[idx + 2] = mag;
                    outBytes[idx + 3] = 255;
                }
            }

            SKBitmap result = new(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
            Marshal.Copy(outBytes, 0, result.GetPixels(), outBytes.Length);
            return new ValueTask<ValueRef>(ValueRef.FromImage(result));
        }
        finally
        {
            owned?.Dispose();
        }
    }
}
