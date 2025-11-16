namespace DatumIngest.Functions.Image;

using DatumIngest.Functions;
using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Returns image dimensions as a vector, with layout controlled by a format string.
/// <c>dimensions(img, format)</c> accepts Image or UInt8Array and a format string.
/// Supported formats: <c>'HWC'</c> → [height, width, channels],
/// <c>'CHW'</c> → [channels, height, width], <c>'WH'</c> → [width, height],
/// <c>'WHC'</c> → [width, height, channels].
/// </summary>
public sealed class ImageDimensionsFunction : IScalarFunction, IImagePipelineSink
{
    /// <inheritdoc />
    public string Name => "dimensions";

    /// <inheritdoc cref="IImagePipelineSink.ResultKind" />
    public DataKind ResultKind => DataKind.Vector;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("dimensions() requires exactly 2 arguments: image, format.");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException(
                $"dimensions() first argument must be Image or UInt8Array, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException(
                $"dimensions() second argument must be String (format), got {argumentKinds[1]}.");
        }

        return DataKind.Vector;
    }

    /// <inheritdoc />
    public void ValidateAuxiliaryArguments(ReadOnlySpan<DataKind> auxiliaryKinds)
    {
        if (auxiliaryKinds.Length != 1)
        {
            throw new ArgumentException(
                $"dimensions() requires exactly 1 auxiliary argument (format); got {auxiliaryKinds.Length}.");
        }

        if (auxiliaryKinds[0] != DataKind.Unknown && auxiliaryKinds[0] != DataKind.String)
        {
            throw new ArgumentException(
                $"dimensions() format must be String, got {auxiliaryKinds[0]}.");
        }
    }

    /// <inheritdoc cref="IImagePipelineSink.Reduce" />
    public DataValue Reduce(SKBitmap input, ReadOnlySpan<DataValue> auxiliaryArgs, IValueStore targetStore)
    {
        string format = auxiliaryArgs[0].AsString().ToUpperInvariant();

        // Match the per-color-type channel mapping that ImageChannelsFunction uses.
        int channels = input.ColorType switch
        {
            SKColorType.Alpha8 or SKColorType.Gray8 => 1,
            SKColorType.Rg88 or SKColorType.Rg1616 or SKColorType.RgF16 => 2,
            SKColorType.Rgb565 or SKColorType.Rgb888x => 3,
            _ => 4,
        };

        float[] result = format switch
        {
            "HWC" => [input.Height, input.Width, channels],
            "CHW" => [channels, input.Height, input.Width],
            "WH" => [input.Width, input.Height],
            "WHC" => [input.Width, input.Height, channels],
            _ => throw new ArgumentException(
                $"dimensions() unknown format '{format}'. Supported: HWC, CHW, WH, WHC.")
        };

        return DataValue.FromVector(result, targetStore);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments) =>
        throw new InvalidOperationException(
            "dimensions() must be lowered to a FusedImagePipelineExpression at plan time " +
            "and should never reach the runtime evaluator. This indicates the " +
            "ImagePipelineLowerer pass did not run, or ran but failed to lower this call.");
}
