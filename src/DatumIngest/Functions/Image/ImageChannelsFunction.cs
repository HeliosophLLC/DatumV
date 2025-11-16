namespace DatumIngest.Functions.Image;

using DatumIngest.Functions;
using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Returns the number of color channels in an image by parsing the image header.
/// <c>channels(img)</c> accepts Image or UInt8Array.
/// Returns 1 for grayscale, 3 for RGB, 4 for RGBA, etc.
/// </summary>
public sealed class ImageChannelsFunction : IScalarFunction, IImagePipelineSink
{
    /// <inheritdoc />
    public string Name => "channels";

    /// <inheritdoc cref="IImagePipelineSink.ResultKind" />
    public DataKind ResultKind => DataKind.Float32;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("channels() requires exactly 1 argument.");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException($"channels() requires Image or UInt8Array, got {argumentKinds[0]}.");
        }

        return DataKind.Float32;
    }

    /// <inheritdoc />
    public void ValidateAuxiliaryArguments(ReadOnlySpan<DataKind> auxiliaryKinds)
    {
        if (auxiliaryKinds.Length != 0)
        {
            throw new ArgumentException(
                $"channels() takes no auxiliary arguments in pipeline form; got {auxiliaryKinds.Length}.");
        }
    }

    /// <inheritdoc cref="IImagePipelineSink.Reduce" />
    public DataValue Reduce(SKBitmap input, ReadOnlySpan<DataValue> auxiliaryArgs, IValueStore targetStore)
    {
        // Use the SKColorType to derive a channel count consistent with the header parser.
        int channels = input.ColorType switch
        {
            SKColorType.Alpha8 or SKColorType.Gray8 => 1,
            SKColorType.Rg88 or SKColorType.Rg1616 or SKColorType.RgF16 => 2,
            SKColorType.Rgb565 or SKColorType.Rgb888x => 3,
            _ => 4, // Rgba8888, Bgra8888, Rgba1010102, RgbaF16, RgbaF32, etc.
        };
        return DataValue.FromFloat32(channels);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments) =>
        throw new InvalidOperationException(
            "channels() must be lowered to a FusedImagePipelineExpression at plan time " +
            "and should never reach the runtime evaluator. This indicates the " +
            "ImagePipelineLowerer pass did not run, or ran but failed to lower this call.");
}
