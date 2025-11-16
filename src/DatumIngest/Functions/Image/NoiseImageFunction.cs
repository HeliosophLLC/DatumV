namespace DatumIngest.Functions.Image;

using DatumIngest.Functions;
using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Adds noise to an image. Supports Gaussian and salt-and-pepper noise models.
/// <c>noise(img, val)</c>, <c>noise(img, type, val)</c>, or <c>noise(img, type, val, format)</c>.
/// The two-argument form defaults to Gaussian noise.
/// Type <c>'gaussian'</c>: additive Gaussian noise where <c>val</c> is standard deviation (0–255 scale).
/// Type <c>'salt_pepper'</c>: randomly sets <c>val</c> ratio of pixels to black or white.
/// The optional format argument controls output encoding (<c>'jpeg'</c>, <c>'png'</c>, <c>'webp'</c>).
/// </summary>
public sealed class NoiseImageFunction : IScalarFunction, ICostAwareFunction, IImagePipelineFunction
{
    /// <inheritdoc />
    public string Name => "noise";

    /// <inheritdoc />
    public int QueryUnitCost => 50;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (2 or 3 or 4))
        {
            throw new ArgumentException(
                "noise() requires 2–4 arguments: image, val or image, type, val[, format].");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException(
                $"noise() first argument must be Image or UInt8Array, got {argumentKinds[0]}.");
        }

        // Two-argument form: noise(image, value) — defaults to gaussian.
        if (argumentKinds.Length == 2)
        {
            if (!DataValue.IsNumericScalarKind(argumentKinds[1]))
            {
                throw new ArgumentException(
                    $"noise() second argument (value) must be numeric, got {argumentKinds[1]}.");
            }

            return DataKind.Image;
        }

        if (argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException(
                $"noise() second argument (type) must be String, got {argumentKinds[1]}.");
        }

        if (!DataValue.IsNumericScalarKind(argumentKinds[2]))
        {
            throw new ArgumentException(
                $"noise() third argument (value) must be numeric, got {argumentKinds[2]}.");
        }

        if (argumentKinds.Length == 4 && argumentKinds[3] != DataKind.String)
        {
            throw new ArgumentException(
                $"noise() fourth argument (format) must be String, got {argumentKinds[3]}.");
        }

        return DataKind.Image;
    }

    /// <inheritdoc />
    public void ValidateAuxiliaryArguments(ReadOnlySpan<DataKind> auxiliaryKinds)
    {
        // Pipeline form drops the implicit image arg. Auxiliary shapes:
        //   [value]                          -> defaults to gaussian
        //   [type, value]                    -> explicit type
        //   [type, value, format]            -> explicit type with format
        if (auxiliaryKinds.Length is not (1 or 2 or 3))
        {
            throw new ArgumentException(
                "noise() requires 1-3 auxiliary arguments: value or type, value[, format].");
        }

        if (auxiliaryKinds.Length == 1)
        {
            if (auxiliaryKinds[0] != DataKind.Unknown && !DataValue.IsNumericScalarKind(auxiliaryKinds[0]))
            {
                throw new ArgumentException(
                    $"noise() value must be numeric, got {auxiliaryKinds[0]}.");
            }
            return;
        }

        if (auxiliaryKinds[0] != DataKind.Unknown && auxiliaryKinds[0] != DataKind.String)
        {
            throw new ArgumentException(
                $"noise() type must be String, got {auxiliaryKinds[0]}.");
        }

        if (auxiliaryKinds[1] != DataKind.Unknown && !DataValue.IsNumericScalarKind(auxiliaryKinds[1]))
        {
            throw new ArgumentException(
                $"noise() value must be numeric, got {auxiliaryKinds[1]}.");
        }

        if (auxiliaryKinds.Length == 3
            && auxiliaryKinds[2] != DataKind.Unknown
            && auxiliaryKinds[2] != DataKind.String)
        {
            throw new ArgumentException(
                $"noise() format must be String, got {auxiliaryKinds[2]}.");
        }
    }

    /// <inheritdoc />
    public SKBitmap Apply(SKBitmap input, ReadOnlySpan<DataValue> auxiliaryArgs)
    {
        string noiseType;
        float value;

        if (auxiliaryArgs.Length == 1)
        {
            noiseType = "GAUSSIAN";
            value = auxiliaryArgs[0].ToFloat();
        }
        else
        {
            noiseType = auxiliaryArgs[0].AsString().ToUpperInvariant();
            value = auxiliaryArgs[1].ToFloat();
        }

        // Always copy because noise modifies pixels in place; we must not mutate the caller's bitmap.
        SKBitmap rgba = input.ColorType == SKColorType.Rgba8888
            ? input.Copy()
            : input.Copy(SKColorType.Rgba8888);

        nint pixelPtr = rgba.GetPixels();
        int totalPixels = rgba.Width * rgba.Height;

        switch (noiseType)
        {
            case "GAUSSIAN":
                ApplyGaussianNoise(pixelPtr, totalPixels, value);
                break;

            case "SALT_PEPPER":
                ApplySaltAndPepperNoise(pixelPtr, totalPixels, value);
                break;

            default:
                rgba.Dispose();
                throw new ArgumentException(
                    $"noise() unknown noise type '{noiseType}'. Supported: gaussian, salt_pepper.");
        }

        return rgba;
    }

    /// <inheritdoc />
    public SKEncodedImageFormat? FormatOverride(ReadOnlySpan<DataValue> auxiliaryArgs)
    {
        // Only the 3-aux form (type, value, format) carries a format string.
        if (auxiliaryArgs.Length < 3 || auxiliaryArgs[2].IsNull)
        {
            return null;
        }
        return ImageEncoder.ParseFormatString(auxiliaryArgs[2].AsString());
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments) =>
        throw new InvalidOperationException(
            "noise() must be lowered to a FusedImagePipelineExpression at plan time " +
            "and should never reach the runtime evaluator. This indicates the " +
            "ImagePipelineLowerer pass did not run, or ran but failed to lower this call.");

    private static void ApplyGaussianNoise(nint pixelPtr, int totalPixels, float standardDeviation)
    {
        Random random = new();
        int totalBytes = totalPixels * 4;

        unsafe
        {
            byte* pixels = (byte*)pixelPtr;

            for (int i = 0; i < totalBytes; i += 4)
            {
                // Box-Muller transform for Gaussian samples
                double u1 = 1.0 - random.NextDouble();
                double u2 = random.NextDouble();
                double gaussian = System.Math.Sqrt(-2.0 * System.Math.Log(u1))
                                  * System.Math.Sin(2.0 * System.Math.PI * u2);

                float noiseValue = (float)(gaussian * standardDeviation);

                // Apply to R, G, B channels; leave alpha unchanged
                pixels[i] = ClampToByte(pixels[i] + noiseValue);
                pixels[i + 1] = ClampToByte(pixels[i + 1] + noiseValue);
                pixels[i + 2] = ClampToByte(pixels[i + 2] + noiseValue);
            }
        }
    }

    private static void ApplySaltAndPepperNoise(nint pixelPtr, int totalPixels, float ratio)
    {
        Random random = new();
        int totalBytes = totalPixels * 4;

        unsafe
        {
            byte* pixels = (byte*)pixelPtr;

            for (int i = 0; i < totalBytes; i += 4)
            {
                if (random.NextDouble() >= ratio)
                {
                    continue;
                }

                // Randomly choose salt (255) or pepper (0)
                byte saltOrPepper = random.NextDouble() < 0.5 ? (byte)0 : (byte)255;

                pixels[i] = saltOrPepper;
                pixels[i + 1] = saltOrPepper;
                pixels[i + 2] = saltOrPepper;
            }
        }
    }

    private static byte ClampToByte(float value)
    {
        if (value <= 0f)
        {
            return 0;
        }

        if (value >= 255f)
        {
            return 255;
        }

        return (byte)value;
    }

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result) =>
        ImageCostHelper.ComputeSupplementalCost(arguments);

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result, in InvocationFrame frame) =>
        ImageCostHelper.ComputeSupplementalCost(arguments, in frame);
}
