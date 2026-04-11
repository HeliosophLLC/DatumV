using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Image;

/// <summary>
/// <c>image_brightness_mean(img) → Float32</c>. Mean BT.601 luminance across
/// all pixels, in the range 0–255. Ignores the alpha channel.
/// </summary>
public sealed class ImageBrightnessMeanFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "image_brightness_mean";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Mean BT.601 luminance across all pixels (0–255). Alpha is ignored.";

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
        FunctionMetadata.Validate<ImageBrightnessMeanFunction>(argumentKinds);

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
            int totalPixels = rgba.Width * rgba.Height;
            if (totalPixels == 0)
            {
                return new ValueTask<ValueRef>(ValueRef.FromFloat32(0f));
            }

            ReadOnlySpan<byte> pixels = rgba.GetPixelSpan();
            double sum = 0.0;
            for (int i = 0; i < totalPixels; i++)
            {
                int o = i * 4;
                sum += pixels[o] * ImagePixelAccess.Bt601RedWeight
                     + pixels[o + 1] * ImagePixelAccess.Bt601GreenWeight
                     + pixels[o + 2] * ImagePixelAccess.Bt601BlueWeight;
            }
            float mean = (float)(sum / totalPixels);
            return new ValueTask<ValueRef>(ValueRef.FromFloat32(mean));
        }
        finally
        {
            owned?.Dispose();
        }
    }
}

/// <summary>
/// <c>image_brightness_std(img) → Float32</c>. Standard deviation of BT.601
/// luminance across all pixels (population std, divisor = N). Ignores alpha.
/// </summary>
public sealed class ImageBrightnessStdFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "image_brightness_std";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Population standard deviation of BT.601 luminance across all pixels. Alpha is ignored.";

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
        FunctionMetadata.Validate<ImageBrightnessStdFunction>(argumentKinds);

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
            int totalPixels = rgba.Width * rgba.Height;
            if (totalPixels == 0)
            {
                return new ValueTask<ValueRef>(ValueRef.FromFloat32(0f));
            }

            ReadOnlySpan<byte> pixels = rgba.GetPixelSpan();
            double sum = 0.0;
            for (int i = 0; i < totalPixels; i++)
            {
                int o = i * 4;
                sum += pixels[o] * ImagePixelAccess.Bt601RedWeight
                     + pixels[o + 1] * ImagePixelAccess.Bt601GreenWeight
                     + pixels[o + 2] * ImagePixelAccess.Bt601BlueWeight;
            }
            double mean = sum / totalPixels;

            double squaredDiffs = 0.0;
            for (int i = 0; i < totalPixels; i++)
            {
                int o = i * 4;
                double luminance = pixels[o] * ImagePixelAccess.Bt601RedWeight
                                 + pixels[o + 1] * ImagePixelAccess.Bt601GreenWeight
                                 + pixels[o + 2] * ImagePixelAccess.Bt601BlueWeight;
                double diff = luminance - mean;
                squaredDiffs += diff * diff;
            }
            float std = (float)System.Math.Sqrt(squaredDiffs / totalPixels);
            return new ValueTask<ValueRef>(ValueRef.FromFloat32(std));
        }
        finally
        {
            owned?.Dispose();
        }
    }
}
