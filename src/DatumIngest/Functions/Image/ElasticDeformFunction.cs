namespace DatumIngest.Functions.Image;

using DatumIngest.Functions;
using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Applies elastic deformation to an image (Simard et al. approach).
/// <c>elastic_deform(img, alpha, sigma)</c> or <c>elastic_deform(img, alpha, sigma, format)</c>.
/// Generates a random displacement field, smooths it with a Gaussian kernel of radius
/// <c>sigma</c>, and scales by <c>alpha</c>. Uses bilinear interpolation for sub-pixel sampling.
/// </summary>
public sealed class ElasticDeformFunction : IScalarFunction, ICostAwareFunction
{
    /// <inheritdoc />
    public string Name => "elastic_deform";

    /// <inheritdoc />
    public int QueryUnitCost => 50;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (3 or 4))
        {
            throw new ArgumentException(
                "elastic_deform() requires 3 or 4 arguments: image, alpha, sigma[, format].");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException(
                $"elastic_deform() first argument must be Image or UInt8Array, got {argumentKinds[0]}.");
        }

        if (argumentKinds.Length == 4 && argumentKinds[3] != DataKind.String)
        {
            throw new ArgumentException(
                $"elastic_deform() fourth argument (format) must be String, got {argumentKinds[3]}.");
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
        float alpha = arguments[1].AsFloat32();
        float sigma = arguments[2].AsFloat32();

        string? formatOverride = arguments.Length == 4 ? arguments[3].AsString() : null;
        SKEncodedImageFormat outputFormat = ImageEncoder.ResolveFormat(inputHandle, formatOverride);

        SKBitmap original = inputHandle.GetBitmap("elastic_deform");

        using SKBitmap? converted = original.ColorType != SKColorType.Rgba8888
            ? ConvertToRgba8888(original)
            : null;
        SKBitmap rgba = converted ?? original;

        int width = rgba.Width;
        int height = rgba.Height;

        // Generate random displacement fields
        Random random = new();
        float[] displacementX = new float[width * height];
        float[] displacementY = new float[width * height];

        for (int i = 0; i < width * height; i++)
        {
            displacementX[i] = (float)(random.NextDouble() * 2.0 - 1.0);
            displacementY[i] = (float)(random.NextDouble() * 2.0 - 1.0);
        }

        // Smooth displacement fields with Gaussian kernel
        int kernelRadius = System.Math.Max(1, (int)System.Math.Ceiling(sigma * 3));
        float[] kernel = BuildGaussianKernel(kernelRadius, sigma);

        float[] smoothedX = ApplyGaussianSmoothing(displacementX, width, height, kernel, kernelRadius);
        float[] smoothedY = ApplyGaussianSmoothing(displacementY, width, height, kernel, kernelRadius);

        // Scale by alpha
        for (int i = 0; i < width * height; i++)
        {
            smoothedX[i] *= alpha;
            smoothedY[i] *= alpha;
        }

        // Apply displacement with bilinear interpolation
        SKBitmap result = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        nint sourcePointer = rgba.GetPixels();
        nint resultPointer = result.GetPixels();

        unsafe
        {
            byte* source = (byte*)sourcePointer;
            byte* destination = (byte*)resultPointer;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = y * width + x;
                    float sourceX = x + smoothedX[index];
                    float sourceY = y + smoothedY[index];

                    BilinearSample(source, width, height, sourceX, sourceY,
                        destination, (y * width + x) * 4);
                }
            }
        }

        return DataValue.FromImageHandle(new ImageHandle(result, outputFormat));
    }

    private static float[] BuildGaussianKernel(int radius, float sigma)
    {
        int size = 2 * radius + 1;
        float[] kernel = new float[size];
        float sum = 0f;

        for (int i = 0; i < size; i++)
        {
            float distance = i - radius;
            kernel[i] = (float)System.Math.Exp(-(distance * distance) / (2f * sigma * sigma));
            sum += kernel[i];
        }

        // Normalize
        for (int i = 0; i < size; i++)
        {
            kernel[i] /= sum;
        }

        return kernel;
    }

    private static float[] ApplyGaussianSmoothing(
        float[] field, int width, int height, float[] kernel, int kernelRadius)
    {
        float[] temporary = new float[width * height];
        float[] result = new float[width * height];

        // Horizontal pass
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float sum = 0f;

                for (int k = -kernelRadius; k <= kernelRadius; k++)
                {
                    int sampleX = System.Math.Clamp(x + k, 0, width - 1);
                    sum += field[y * width + sampleX] * kernel[k + kernelRadius];
                }

                temporary[y * width + x] = sum;
            }
        }

        // Vertical pass
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float sum = 0f;

                for (int k = -kernelRadius; k <= kernelRadius; k++)
                {
                    int sampleY = System.Math.Clamp(y + k, 0, height - 1);
                    sum += temporary[sampleY * width + x] * kernel[k + kernelRadius];
                }

                result[y * width + x] = sum;
            }
        }

        return result;
    }

    private static unsafe void BilinearSample(
        byte* source, int width, int height, float sourceX, float sourceY,
        byte* destination, int destinationOffset)
    {
        // Clamp source coordinates to valid range
        sourceX = System.Math.Clamp(sourceX, 0, width - 1.001f);
        sourceY = System.Math.Clamp(sourceY, 0, height - 1.001f);

        int x0 = (int)sourceX;
        int y0 = (int)sourceY;
        int x1 = System.Math.Min(x0 + 1, width - 1);
        int y1 = System.Math.Min(y0 + 1, height - 1);

        float fractionX = sourceX - x0;
        float fractionY = sourceY - y0;

        for (int channel = 0; channel < 4; channel++)
        {
            float topLeft = source[(y0 * width + x0) * 4 + channel];
            float topRight = source[(y0 * width + x1) * 4 + channel];
            float bottomLeft = source[(y1 * width + x0) * 4 + channel];
            float bottomRight = source[(y1 * width + x1) * 4 + channel];

            float top = topLeft + (topRight - topLeft) * fractionX;
            float bottom = bottomLeft + (bottomRight - bottomLeft) * fractionX;
            float value = top + (bottom - top) * fractionY;

            destination[destinationOffset + channel] = value <= 0f
                ? (byte)0
                : value >= 255f
                    ? (byte)255
                    : (byte)value;
        }
    }

    private static SKBitmap ConvertToRgba8888(SKBitmap source)
    {
        SKBitmap converted = new(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKCanvas canvas = new(converted);
        canvas.DrawBitmap(source, 0, 0);
        return converted;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Image);
        }

        ImageHandle inputHandle = input.GetImageHandle(frame.Source, frame.SidecarRegistry);
        float alpha = arguments[1].AsFloat32();
        float sigma = arguments[2].AsFloat32();

        string? formatOverride = arguments.Length == 4 ? arguments[3].AsString(frame.Source) : null;
        SKEncodedImageFormat outputFormat = ImageEncoder.ResolveFormat(inputHandle, formatOverride);

        SKBitmap original = inputHandle.GetBitmap("elastic_deform");

        using SKBitmap? converted = original.ColorType != SKColorType.Rgba8888
            ? ConvertToRgba8888(original)
            : null;
        SKBitmap rgba = converted ?? original;

        int width = rgba.Width;
        int height = rgba.Height;

        // Generate random displacement fields
        Random random = new();
        float[] displacementX = new float[width * height];
        float[] displacementY = new float[width * height];

        for (int i = 0; i < width * height; i++)
        {
            displacementX[i] = (float)(random.NextDouble() * 2.0 - 1.0);
            displacementY[i] = (float)(random.NextDouble() * 2.0 - 1.0);
        }

        // Smooth displacement fields with Gaussian kernel
        int kernelRadius = System.Math.Max(1, (int)System.Math.Ceiling(sigma * 3));
        float[] kernel = BuildGaussianKernel(kernelRadius, sigma);

        float[] smoothedX = ApplyGaussianSmoothing(displacementX, width, height, kernel, kernelRadius);
        float[] smoothedY = ApplyGaussianSmoothing(displacementY, width, height, kernel, kernelRadius);

        // Scale by alpha
        for (int i = 0; i < width * height; i++)
        {
            smoothedX[i] *= alpha;
            smoothedY[i] *= alpha;
        }

        // Apply displacement with bilinear interpolation
        SKBitmap result = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        nint sourcePointer = rgba.GetPixels();
        nint resultPointer = result.GetPixels();

        unsafe
        {
            byte* source = (byte*)sourcePointer;
            byte* destination = (byte*)resultPointer;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = y * width + x;
                    float sourceX = x + smoothedX[index];
                    float sourceY = y + smoothedY[index];

                    BilinearSample(source, width, height, sourceX, sourceY,
                        destination, (y * width + x) * 4);
                }
            }
        }

        return DataValue.FromImageHandle(new ImageHandle(result, outputFormat), frame.Target);
    }

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result) =>
        ImageCostHelper.ComputeSupplementalCost(arguments);

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result, in InvocationFrame frame) =>
        ImageCostHelper.ComputeSupplementalCost(arguments, in frame);
}
