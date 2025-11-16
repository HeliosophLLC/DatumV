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
public sealed class ElasticDeformFunction : IScalarFunction, ICostAwareFunction, IImagePipelineFunction
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

        if (!DataValue.IsNumericScalarKind(argumentKinds[1]))
        {
            throw new ArgumentException(
                $"elastic_deform() second argument (alpha) must be numeric, got {argumentKinds[1]}.");
        }

        if (!DataValue.IsNumericScalarKind(argumentKinds[2]))
        {
            throw new ArgumentException(
                $"elastic_deform() third argument (sigma) must be numeric, got {argumentKinds[2]}.");
        }

        if (argumentKinds.Length == 4 && argumentKinds[3] != DataKind.String)
        {
            throw new ArgumentException(
                $"elastic_deform() fourth argument (format) must be String, got {argumentKinds[3]}.");
        }

        return DataKind.Image;
    }

    /// <inheritdoc />
    public void ValidateAuxiliaryArguments(ReadOnlySpan<DataKind> auxiliaryKinds)
    {
        if (auxiliaryKinds.Length is not (2 or 3))
        {
            throw new ArgumentException(
                "elastic_deform() requires 2 or 3 auxiliary arguments: alpha, sigma[, format].");
        }

        if (auxiliaryKinds[0] != DataKind.Unknown && !DataValue.IsNumericScalarKind(auxiliaryKinds[0]))
        {
            throw new ArgumentException(
                $"elastic_deform() alpha must be numeric, got {auxiliaryKinds[0]}.");
        }

        if (auxiliaryKinds[1] != DataKind.Unknown && !DataValue.IsNumericScalarKind(auxiliaryKinds[1]))
        {
            throw new ArgumentException(
                $"elastic_deform() sigma must be numeric, got {auxiliaryKinds[1]}.");
        }

        if (auxiliaryKinds.Length == 3
            && auxiliaryKinds[2] != DataKind.Unknown
            && auxiliaryKinds[2] != DataKind.String)
        {
            throw new ArgumentException(
                $"elastic_deform() format must be String, got {auxiliaryKinds[2]}.");
        }
    }

    /// <inheritdoc />
    public SKBitmap Apply(SKBitmap input, ReadOnlySpan<DataValue> auxiliaryArgs)
    {
        float alpha = auxiliaryArgs[0].ToFloat();
        float sigma = auxiliaryArgs[1].ToFloat();

        using SKBitmap? converted = input.ColorType != SKColorType.Rgba8888
            ? ConvertToRgba8888(input)
            : null;
        SKBitmap rgba = converted ?? input;

        int width = rgba.Width;
        int height = rgba.Height;

        Random random = new();
        float[] displacementX = new float[width * height];
        float[] displacementY = new float[width * height];

        for (int i = 0; i < width * height; i++)
        {
            displacementX[i] = (float)(random.NextDouble() * 2.0 - 1.0);
            displacementY[i] = (float)(random.NextDouble() * 2.0 - 1.0);
        }

        int kernelRadius = System.Math.Max(1, (int)System.Math.Ceiling(sigma * 3));
        float[] kernel = BuildGaussianKernel(kernelRadius, sigma);

        float[] smoothedX = ApplyGaussianSmoothing(displacementX, width, height, kernel, kernelRadius);
        float[] smoothedY = ApplyGaussianSmoothing(displacementY, width, height, kernel, kernelRadius);

        for (int i = 0; i < width * height; i++)
        {
            smoothedX[i] *= alpha;
            smoothedY[i] *= alpha;
        }

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

        return result;
    }

    /// <inheritdoc />
    public SKEncodedImageFormat? FormatOverride(ReadOnlySpan<DataValue> auxiliaryArgs)
    {
        if (auxiliaryArgs.Length < 3 || auxiliaryArgs[2].IsNull)
        {
            return null;
        }
        return ImageEncoder.ParseFormatString(auxiliaryArgs[2].AsString());
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments) =>
        throw new InvalidOperationException(
            "elastic_deform() must be lowered to a FusedImagePipelineExpression at plan time " +
            "and should never reach the runtime evaluator. This indicates the " +
            "ImagePipelineLowerer pass did not run, or ran but failed to lower this call.");

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
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result) =>
        ImageCostHelper.ComputeSupplementalCost(arguments);

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result, in InvocationFrame frame) =>
        ImageCostHelper.ComputeSupplementalCost(arguments, in frame);
}
