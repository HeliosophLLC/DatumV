namespace DatumIngest.Functions.Image;

using DatumIngest.Functions;
using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Returns the total number of pixels (width × height) by parsing the image header.
/// <c>pixel_count(img)</c> accepts Image or UInt8Array.
/// </summary>
public sealed class ImagePixelCountFunction : IScalarFunction, IImagePipelineSink
{
    /// <inheritdoc />
    public string Name => "pixel_count";

    /// <inheritdoc cref="IImagePipelineSink.ResultKind" />
    public DataKind ResultKind => DataKind.Float32;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("pixel_count() requires exactly 1 argument.");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException($"pixel_count() requires Image or UInt8Array, got {argumentKinds[0]}.");
        }

        return DataKind.Float32;
    }

    /// <inheritdoc />
    public void ValidateAuxiliaryArguments(ReadOnlySpan<DataKind> auxiliaryKinds)
    {
        if (auxiliaryKinds.Length != 0)
        {
            throw new ArgumentException(
                $"pixel_count() takes no auxiliary arguments in pipeline form; got {auxiliaryKinds.Length}.");
        }
    }

    /// <inheritdoc cref="IImagePipelineSink.Reduce" />
    public DataValue Reduce(SKBitmap input, ReadOnlySpan<DataValue> auxiliaryArgs, IValueStore targetStore)
    {
        return DataValue.FromFloat32((float)input.Width * input.Height);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments) =>
        throw new InvalidOperationException(
            "pixel_count() must be lowered to a FusedImagePipelineExpression at plan time " +
            "and should never reach the runtime evaluator. This indicates the " +
            "ImagePipelineLowerer pass did not run, or ran but failed to lower this call.");
}
