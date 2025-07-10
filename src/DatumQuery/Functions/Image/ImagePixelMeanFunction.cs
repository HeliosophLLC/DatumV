namespace Axon.QueryEngine.Functions.Image;

using Axon.QueryEngine.Model;

using SkiaSharp;

/// <summary>
/// Computes the mean pixel value per channel.
/// <c>image_pixel_mean(img)</c> returns the overall mean across all channels as a scalar.
/// <c>image_pixel_mean(img, channels)</c> returns per-channel means as a vector,
/// where <c>channels</c> is a vector of channel indices (0=R, 1=G, 2=B, 3=A).
/// </summary>
public sealed class ImagePixelMeanFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "image_pixel_mean";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (1 or 2))
        {
            throw new ArgumentException("image_pixel_mean() requires 1 or 2 arguments: image[, channels].");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException(
                $"image_pixel_mean() first argument must be Image or UInt8Array, got {argumentKinds[0]}.");
        }

        if (argumentKinds.Length == 2 && argumentKinds[1] != DataKind.Vector)
        {
            throw new ArgumentException(
                $"image_pixel_mean() second argument (channels) must be Vector, got {argumentKinds[1]}.");
        }

        return argumentKinds.Length == 1 ? DataKind.Scalar : DataKind.Vector;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            DataKind resultKind = arguments.Length == 1 ? DataKind.Scalar : DataKind.Vector;
            return DataValue.Null(resultKind);
        }

        ImageHandle inputHandle = input.GetImageHandle();
        SKBitmap bitmap = inputHandle.GetBitmap("image_pixel_mean");

        using SKBitmap rgba = bitmap.ColorType == SKColorType.Rgba8888
            ? bitmap
            : ConvertToRgba8888(bitmap);

        int totalPixels = rgba.Width * rgba.Height;
        nint pixelPointer = rgba.GetPixels();

        if (arguments.Length == 1)
        {
            // No channels specified — mean across all RGBA channels
            return ComputeOverallMean(pixelPointer, totalPixels);
        }

        float[] channelIndices = arguments[1].AsVector();
        return ComputePerChannelMean(pixelPointer, totalPixels, channelIndices);
    }

    private static DataValue ComputeOverallMean(nint pixelPointer, int totalPixels)
    {
        double sum = 0.0;
        int totalElements = totalPixels * 4;

        unsafe
        {
            byte* pixels = (byte*)pixelPointer;

            for (int i = 0; i < totalElements; i++)
            {
                sum += pixels[i];
            }
        }

        float mean = (float)(sum / totalElements);
        return DataValue.FromScalar(mean);
    }

    private static DataValue ComputePerChannelMean(nint pixelPointer, int totalPixels, float[] channelIndices)
    {
        double[] sums = new double[channelIndices.Length];

        unsafe
        {
            byte* pixels = (byte*)pixelPointer;

            for (int i = 0; i < totalPixels; i++)
            {
                int offset = i * 4;

                for (int c = 0; c < channelIndices.Length; c++)
                {
                    int channelIndex = (int)channelIndices[c];

                    if (channelIndex is < 0 or > 3)
                    {
                        throw new ArgumentException(
                            $"image_pixel_mean() channel index {channelIndex} is out of range (0–3).");
                    }

                    sums[c] += pixels[offset + channelIndex];
                }
            }
        }

        float[] means = new float[channelIndices.Length];

        for (int c = 0; c < channelIndices.Length; c++)
        {
            means[c] = (float)(sums[c] / totalPixels);
        }

        return DataValue.FromVector(means);
    }

    private static SKBitmap ConvertToRgba8888(SKBitmap source)
    {
        SKBitmap converted = new(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKCanvas canvas = new(converted);
        canvas.DrawBitmap(source, 0, 0);
        return converted;
    }
}
