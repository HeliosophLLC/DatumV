namespace DatumIngest.Functions.Image;

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
public sealed class NoiseImageFunction : IScalarFunction, ICostAwareFunction
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
            return DataKind.Image;
        }

        if (argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException(
                $"noise() second argument (type) must be String, got {argumentKinds[1]}.");
        }

        if (argumentKinds.Length == 4 && argumentKinds[3] != DataKind.String)
        {
            throw new ArgumentException(
                $"noise() fourth argument (format) must be String, got {argumentKinds[3]}.");
        }

        return DataKind.Image;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Image);
        }

        ImageHandle inputHandle = input.GetImageHandle();

        // Two-argument form: noise(image, value) — defaults to gaussian.
        string noiseType;
        float value;
        string? formatOverride;

        if (arguments.Length == 2)
        {
            noiseType = "GAUSSIAN";
            value = arguments[1].AsFloat32();
            formatOverride = null;
        }
        else
        {
            noiseType = arguments[1].AsString().ToUpperInvariant();
            value = arguments[2].AsFloat32();
            formatOverride = arguments.Length == 4 ? arguments[3].AsString() : null;
        }
        SKEncodedImageFormat outputFormat = ImageEncoder.ResolveFormat(inputHandle, formatOverride);

        SKBitmap original = inputHandle.GetBitmap("noise");

        // Work in RGBA8888 for consistent pixel access — always copy
        // because noise modifies pixels in place and we must not mutate the input handle's bitmap.
        SKBitmap rgba = original.ColorType == SKColorType.Rgba8888
            ? original.Copy()
            : original.Copy(SKColorType.Rgba8888);

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

        return DataValue.FromImageHandle(new ImageHandle(rgba, outputFormat));
    }

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
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Image);
        }

        ImageHandle inputHandle = input.GetImageHandle(store);

        // Two-argument form: noise(image, value) — defaults to gaussian.
        string noiseType;
        float value;
        string? formatOverride;

        if (arguments.Length == 2)
        {
            noiseType = "GAUSSIAN";
            value = arguments[1].AsFloat32();
            formatOverride = null;
        }
        else
        {
            noiseType = arguments[1].AsString(store).ToUpperInvariant();
            value = arguments[2].AsFloat32();
            formatOverride = arguments.Length == 4 ? arguments[3].AsString(store) : null;
        }
        SKEncodedImageFormat outputFormat = ImageEncoder.ResolveFormat(inputHandle, formatOverride);

        SKBitmap original = inputHandle.GetBitmap("noise");

        // Work in RGBA8888 for consistent pixel access — always copy
        // because noise modifies pixels in place and we must not mutate the input handle's bitmap.
        SKBitmap rgba = original.ColorType == SKColorType.Rgba8888
            ? original.Copy()
            : original.Copy(SKColorType.Rgba8888);

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

        return DataValue.FromImageHandle(new ImageHandle(rgba, outputFormat), store);
    }

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result) =>
        ImageCostHelper.ComputeSupplementalCost(arguments);
}
