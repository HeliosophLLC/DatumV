namespace DatumIngest.Functions.Image;

using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Computes the standard deviation of pixel values per channel.
/// <c>image_pixel_std(img)</c> returns the overall standard deviation across all channels as a scalar.
/// <c>image_pixel_std(img, channels)</c> returns per-channel standard deviations as a vector,
/// where <c>channels</c> is a vector of channel indices (0=R, 1=G, 2=B, 3=A).
/// </summary>
public sealed class ImagePixelStandardDeviationFunction : IScalarFunction, ICostAwareFunction
{
    /// <inheritdoc />
    public string Name => "image_pixel_std";

    /// <inheritdoc />
    public int QueryUnitCost => 10;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (1 or 2))
        {
            throw new ArgumentException("image_pixel_std() requires 1 or 2 arguments: image[, channels].");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException(
                $"image_pixel_std() first argument must be Image or UInt8Array, got {argumentKinds[0]}.");
        }

        if (argumentKinds.Length == 2 && argumentKinds[1] != DataKind.Vector)
        {
            throw new ArgumentException(
                $"image_pixel_std() second argument (channels) must be Vector, got {argumentKinds[1]}.");
        }

        return argumentKinds.Length == 1 ? DataKind.Float32 : DataKind.Vector;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            DataKind resultKind = arguments.Length == 1 ? DataKind.Float32 : DataKind.Vector;
            return DataValue.Null(resultKind);
        }

        ImageHandle inputHandle = input.GetImageHandle();
        SKBitmap bitmap = inputHandle.GetBitmap("image_pixel_std");

        using SKBitmap? converted = bitmap.ColorType != SKColorType.Rgba8888
            ? ConvertToRgba8888(bitmap)
            : null;
        SKBitmap rgba = converted ?? bitmap;

        int totalPixels = rgba.Width * rgba.Height;
        nint pixelPointer = rgba.GetPixels();

        if (arguments.Length == 1)
        {
            return ComputeOverallStandardDeviation(pixelPointer, totalPixels);
        }

        float[] channelIndices = arguments[1].AsVector();
        return ComputePerChannelStandardDeviation(pixelPointer, totalPixels, channelIndices);
    }

    private static DataValue ComputeOverallStandardDeviation(nint pixelPointer, int totalPixels)
    {
        int totalElements = totalPixels * 4;
        double sum = 0.0;

        unsafe
        {
            byte* pixels = (byte*)pixelPointer;

            for (int i = 0; i < totalElements; i++)
            {
                sum += pixels[i];
            }

            double mean = sum / totalElements;
            double sumSquaredDifferences = 0.0;

            for (int i = 0; i < totalElements; i++)
            {
                double difference = pixels[i] - mean;
                sumSquaredDifferences += difference * difference;
            }

            double variance = sumSquaredDifferences / totalElements;
            float standardDeviation = (float)System.Math.Sqrt(variance);
            return DataValue.FromFloat32(standardDeviation);
        }
    }

    private static DataValue ComputePerChannelStandardDeviation(
        nint pixelPointer, int totalPixels, float[] channelIndices)
    {
        double[] sums = new double[channelIndices.Length];

        unsafe
        {
            byte* pixels = (byte*)pixelPointer;

            // Pass 1: compute means
            for (int i = 0; i < totalPixels; i++)
            {
                int offset = i * 4;

                for (int c = 0; c < channelIndices.Length; c++)
                {
                    int channelIndex = (int)channelIndices[c];

                    if (channelIndex is < 0 or > 3)
                    {
                        throw new ArgumentException(
                            $"image_pixel_std() channel index {channelIndex} is out of range (0–3).");
                    }

                    sums[c] += pixels[offset + channelIndex];
                }
            }

            double[] means = new double[channelIndices.Length];
            for (int c = 0; c < channelIndices.Length; c++)
            {
                means[c] = sums[c] / totalPixels;
            }

            // Pass 2: compute variance
            double[] sumSquaredDifferences = new double[channelIndices.Length];

            for (int i = 0; i < totalPixels; i++)
            {
                int offset = i * 4;

                for (int c = 0; c < channelIndices.Length; c++)
                {
                    int channelIndex = (int)channelIndices[c];
                    double difference = pixels[offset + channelIndex] - means[c];
                    sumSquaredDifferences[c] += difference * difference;
                }
            }

            float[] standardDeviations = new float[channelIndices.Length];
            for (int c = 0; c < channelIndices.Length; c++)
            {
                double variance = sumSquaredDifferences[c] / totalPixels;
                standardDeviations[c] = (float)System.Math.Sqrt(variance);
            }

            return DataValue.FromVector(standardDeviations);
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
}
