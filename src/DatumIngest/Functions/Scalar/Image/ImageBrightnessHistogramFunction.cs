using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Image;

/// <summary>
/// <c>image_brightness_histogram(img) → Array&lt;Float32&gt;</c>. 256-bin
/// brightness histogram over BT.601 luminance; each element is the pixel
/// count in that bin (bin index = luminance, clamped to <c>[0, 255]</c>).
/// </summary>
public sealed class ImageBrightnessHistogramFunction : IFunction, IScalarFunction
{
    private const int BinCount = 256;

    /// <inheritdoc />
    public static string Name => "image_brightness_histogram";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "256-bin BT.601 luminance histogram. Each element is the pixel count for that bin.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("image", DataKindMatcher.Exact(DataKind.Image))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ImageBrightnessHistogramFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef imgArg = arguments.Span[0];
        if (imgArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.Float32));
        }

        SKBitmap source = imgArg.AsImage();
        SKBitmap rgba = ImagePixelAccess.AsRgba8888(source, out SKBitmap? owned);
        try
        {
            int totalPixels = rgba.Width * rgba.Height;
            float[] histogram = new float[BinCount];

            ReadOnlySpan<byte> pixels = rgba.GetPixelSpan();
            for (int i = 0; i < totalPixels; i++)
            {
                int o = i * 4;
                float luminance = pixels[o] * ImagePixelAccess.Bt601RedWeight
                                + pixels[o + 1] * ImagePixelAccess.Bt601GreenWeight
                                + pixels[o + 2] * ImagePixelAccess.Bt601BlueWeight;
                int bin = (int)luminance;
                if (bin < 0) bin = 0;
                else if (bin >= BinCount) bin = BinCount - 1;
                histogram[bin]++;
            }

            return new ValueTask<ValueRef>(
                ValueRef.FromPrimitiveArray(histogram, DataKind.Float32));
        }
        finally
        {
            owned?.Dispose();
        }
    }
}
