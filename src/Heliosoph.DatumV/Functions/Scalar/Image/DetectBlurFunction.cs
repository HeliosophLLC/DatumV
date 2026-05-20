using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using SkiaSharp;

namespace Heliosoph.DatumV.Functions.Scalar.Image;

/// <summary>
/// <c>detect_blur(img) → Float32</c>. Laplacian variance blur detector:
/// converts the image to BT.601 grayscale, applies a 3×3 Laplacian kernel
/// (excluding the 1-pixel border), and returns the variance of the
/// Laplacian response. Higher = sharper, lower = blurrier. Images smaller
/// than 3×3 return 0 (no interior pixels).
/// </summary>
public sealed class DetectBlurFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "detect_blur";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Laplacian variance blur detector over BT.601 grayscale. Higher values indicate a sharper image.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("image", DataKindMatcher.Exact(DataKind.Image))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DetectBlurFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef imgArg = arguments.Span[0];
        if (imgArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32));
        }

        SKBitmap source = imgArg.AsImage();
        SKBitmap rgba = ImagePixelAccess.AsRgba8888(source, out SKBitmap? owned);
        try
        {
            int width = rgba.Width;
            int height = rgba.Height;
            int interiorCount = (width - 2) * (height - 2);
            if (interiorCount <= 0)
            {
                return new ValueTask<ValueRef>(ValueRef.FromFloat32(0f));
            }

            ReadOnlySpan<byte> pixels = rgba.GetPixelSpan();
            float[] gray = new float[width * height];
            for (int i = 0; i < gray.Length; i++)
            {
                int o = i * 4;
                gray[i] = pixels[o] * ImagePixelAccess.Bt601RedWeight
                        + pixels[o + 1] * ImagePixelAccess.Bt601GreenWeight
                        + pixels[o + 2] * ImagePixelAccess.Bt601BlueWeight;
            }

            double sum = 0.0;
            double sumSq = 0.0;
            for (int y = 1; y < height - 1; y++)
            {
                int rowBase = y * width;
                for (int x = 1; x < width - 1; x++)
                {
                    float laplacian = gray[rowBase - width + x]
                                    + gray[rowBase + width + x]
                                    + gray[rowBase + x - 1]
                                    + gray[rowBase + x + 1]
                                    - 4f * gray[rowBase + x];
                    sum += laplacian;
                    sumSq += (double)laplacian * laplacian;
                }
            }

            double mean = sum / interiorCount;
            double variance = (sumSq / interiorCount) - (mean * mean);
            return new ValueTask<ValueRef>(ValueRef.FromFloat32((float)variance));
        }
        finally
        {
            owned?.Dispose();
        }
    }
}
