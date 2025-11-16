namespace DatumIngest.Functions.Image;

using DatumIngest.Functions;
using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Returns the width of an image in pixels.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Standalone form</strong> — <c>width(img)</c> parses the image header bytes
/// without a full decode, so it's cheap even on multi-megabyte images.
/// </para>
/// <para>
/// <strong>Pipeline form</strong> — when used as a terminal sink inside
/// <c>img(source, f -&gt; ... .width())</c>, reads <see cref="SKBitmap.Width"/> from the
/// already-decoded bitmap that the pipeline threaded through. Faster for chained
/// pipelines (no header re-parse) and works after transforms that change the size
/// (e.g. <c>img(file, f -&gt; resize(f, 64, 64).width())</c> returns 64).
/// </para>
/// </remarks>
public sealed class ImageWidthFunction : IScalarFunction, IImagePipelineSink
{
    /// <inheritdoc />
    public string Name => "width";

    /// <inheritdoc cref="IImagePipelineSink.ResultKind" />
    public DataKind ResultKind => DataKind.Float32;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("width() requires exactly 1 argument.");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException($"width() requires Image or UInt8Array, got {argumentKinds[0]}.");
        }

        return DataKind.Float32;
    }

    /// <inheritdoc />
    public void ValidateAuxiliaryArguments(ReadOnlySpan<DataKind> auxiliaryKinds)
    {
        // Pipeline form: image is the implicit arg, no auxiliaries expected.
        if (auxiliaryKinds.Length != 0)
        {
            throw new ArgumentException(
                $"width() takes no auxiliary arguments in pipeline form; got {auxiliaryKinds.Length}.");
        }
    }

    /// <inheritdoc cref="IImagePipelineSink.Reduce" />
    public DataValue Reduce(SKBitmap input, ReadOnlySpan<DataValue> auxiliaryArgs, IValueStore targetStore)
    {
        return DataValue.FromFloat32(input.Width);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments) =>
        throw new InvalidOperationException(
            "width() must be lowered to a FusedImagePipelineExpression at plan time " +
            "and should never reach the runtime evaluator. This indicates the " +
            "ImagePipelineLowerer pass did not run, or ran but failed to lower this call.");
}
