namespace Axon.QueryEngine.Functions.Image;

using Axon.QueryEngine.Model;

using SkiaSharp;

/// <summary>
/// Applies Sobel edge detection to an image.
/// <c>sobel(img)</c> or <c>sobel(img, format)</c>.
/// Converts to grayscale, applies 3×3 Sobel kernels for horizontal and vertical
/// gradients, and outputs the edge magnitude image (grayscale).
/// </summary>
public sealed class SobelImageFunction : IScalarFunction
{
    // ITU-R BT.601 luminance weights for grayscale conversion
    private const float RedWeight = 0.2126f;
    private const float GreenWeight = 0.7152f;
    private const float BlueWeight = 0.0722f;

    /// <inheritdoc />
    public string Name => "sobel";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (1 or 2))
        {
            throw new ArgumentException("sobel() requires 1 or 2 arguments: image[, format].");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException(
                $"sobel() first argument must be Image or UInt8Array, got {argumentKinds[0]}.");
        }

        if (argumentKinds.Length == 2 && argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException(
                $"sobel() second argument (format) must be String, got {argumentKinds[1]}.");
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

        string? formatOverride = arguments.Length == 2 ? arguments[1].AsString() : null;
        SKEncodedImageFormat outputFormat = ImageEncoder.ResolveFormat(inputHandle, formatOverride);

        SKBitmap original = inputHandle.GetBitmap("sobel");

        using SKBitmap rgba = original.ColorType == SKColorType.Rgba8888
            ? original
            : ConvertToRgba8888(original);

        int width = rgba.Width;
        int height = rgba.Height;
        nint pixelPointer = rgba.GetPixels();

        // Convert to grayscale luminance buffer
        float[] grayscale = new float[width * height];

        unsafe
        {
            byte* pixels = (byte*)pixelPointer;

            for (int i = 0; i < width * height; i++)
            {
                int offset = i * 4;
                grayscale[i] = pixels[offset] * RedWeight
                             + pixels[offset + 1] * GreenWeight
                             + pixels[offset + 2] * BlueWeight;
            }
        }

        // Apply Sobel kernels and produce output bitmap
        // Gx = [-1, 0, 1; -2, 0, 2; -1, 0, 1]
        // Gy = [-1, -2, -1; 0, 0, 0; 1, 2, 1]
        SKBitmap result = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        nint resultPointer = result.GetPixels();

        unsafe
        {
            byte* output = (byte*)resultPointer;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = (y * width + x) * 4;

                    if (y == 0 || y == height - 1 || x == 0 || x == width - 1)
                    {
                        // Border pixels: set to black
                        output[index] = 0;
                        output[index + 1] = 0;
                        output[index + 2] = 0;
                        output[index + 3] = 255;
                        continue;
                    }

                    // Sobel horizontal gradient
                    float gradientX = -grayscale[(y - 1) * width + (x - 1)]
                                    + grayscale[(y - 1) * width + (x + 1)]
                                    - 2f * grayscale[y * width + (x - 1)]
                                    + 2f * grayscale[y * width + (x + 1)]
                                    - grayscale[(y + 1) * width + (x - 1)]
                                    + grayscale[(y + 1) * width + (x + 1)];

                    // Sobel vertical gradient
                    float gradientY = -grayscale[(y - 1) * width + (x - 1)]
                                    - 2f * grayscale[(y - 1) * width + x]
                                    - grayscale[(y - 1) * width + (x + 1)]
                                    + grayscale[(y + 1) * width + (x - 1)]
                                    + 2f * grayscale[(y + 1) * width + x]
                                    + grayscale[(y + 1) * width + (x + 1)];

                    float magnitude = (float)System.Math.Sqrt(gradientX * gradientX + gradientY * gradientY);
                    byte clamped = magnitude >= 255f ? (byte)255 : (byte)magnitude;

                    output[index] = clamped;
                    output[index + 1] = clamped;
                    output[index + 2] = clamped;
                    output[index + 3] = 255;
                }
            }
        }

        return DataValue.FromImageHandle(new ImageHandle(result, outputFormat));
    }

    private static SKBitmap ConvertToRgba8888(SKBitmap source)
    {
        SKBitmap converted = new(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKCanvas canvas = new(converted);
        canvas.DrawBitmap(source, 0, 0);
        return converted;
    }
}
