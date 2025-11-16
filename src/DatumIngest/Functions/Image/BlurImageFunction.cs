namespace DatumIngest.Functions.Image;

using DatumIngest.Functions;
using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Applies a Gaussian blur to an image.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Standalone form</strong> — <c>blur(img, radius)</c> or <c>blur(img, radius, format)</c>.
/// Decodes the source bytes, blurs, re-encodes. <c>radius</c> is the Gaussian sigma in
/// both X and Y. The optional <c>format</c> arg overrides the output encoding
/// (<c>'jpeg'</c>, <c>'png'</c>, <c>'webp'</c>); when omitted the source format is preserved.
/// </para>
/// <para>
/// <strong>Pipeline form</strong> — inside an <c>image(source, lambda)</c> body
/// (<c>image(file, f =&gt; blur(f, 5))</c>), <see cref="Apply"/> threads the live
/// <see cref="SKBitmap"/> through. Decode and encode happen exactly once at the
/// pipeline boundaries regardless of how many transforms are chained.
/// </para>
/// </remarks>
public sealed class BlurImageFunction : IScalarFunction, ICostAwareFunction, IImagePipelineFunction
{
    /// <inheritdoc />
    public string Name => "blur";

    /// <inheritdoc />
    public int QueryUnitCost => 50;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (2 or 3))
        {
            throw new ArgumentException("blur() requires 2 or 3 arguments: image, radius[, format].");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException(
                $"blur() first argument must be Image or UInt8Array, got {argumentKinds[0]}.");
        }

        if (!DataValue.IsNumericScalarKind(argumentKinds[1]))
        {
            throw new ArgumentException(
                $"blur() second argument (radius) must be numeric, got {argumentKinds[1]}.");
        }

        if (argumentKinds.Length == 3 && argumentKinds[2] != DataKind.String)
        {
            throw new ArgumentException(
                $"blur() third argument (format) must be String, got {argumentKinds[2]}.");
        }

        return DataKind.Image;
    }

    /// <inheritdoc />
    public void ValidateAuxiliaryArguments(ReadOnlySpan<DataKind> auxiliaryKinds)
    {
        // Pipeline form drops the implicit image arg; auxiliaries are [radius] or
        // [radius, format].
        if (auxiliaryKinds.Length is not (1 or 2))
        {
            throw new ArgumentException("blur() requires 1 or 2 auxiliary arguments: radius[, format].");
        }

        // Plan-time best-effort: a column-ref or other unresolved expression resolves to
        // Unknown — accept it and let runtime widening (ToFloat) catch real type errors.
        if (auxiliaryKinds[0] != DataKind.Unknown && !DataValue.IsNumericScalarKind(auxiliaryKinds[0]))
        {
            throw new ArgumentException(
                $"blur() radius must be numeric, got {auxiliaryKinds[0]}.");
        }

        if (auxiliaryKinds.Length == 2
            && auxiliaryKinds[1] != DataKind.Unknown
            && auxiliaryKinds[1] != DataKind.String)
        {
            throw new ArgumentException(
                $"blur() format must be String, got {auxiliaryKinds[1]}.");
        }
    }

    /// <inheritdoc />
    public SKBitmap Apply(SKBitmap input, ReadOnlySpan<DataValue> auxiliaryArgs)
    {
        float radius = auxiliaryArgs[0].ToFloat();

        SKBitmap blurred = new(input.Width, input.Height);
        using SKCanvas canvas = new(blurred);
        using SKImageFilter blurFilter = SKImageFilter.CreateBlur(radius, radius);
        using SKPaint paint = new() { ImageFilter = blurFilter };

        canvas.DrawBitmap(input, 0, 0, paint);
        return blurred;
    }

    /// <inheritdoc />
    public SKEncodedImageFormat? FormatOverride(ReadOnlySpan<DataValue> auxiliaryArgs)
    {
        if (auxiliaryArgs.Length < 2 || auxiliaryArgs[1].IsNull)
        {
            return null;
        }
        return ImageEncoder.ParseFormatString(auxiliaryArgs[1].AsString());
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments) =>
        throw new InvalidOperationException(
            "blur() must be lowered to a FusedImagePipelineExpression at plan time " +
            "and should never reach the runtime evaluator. This indicates the " +
            "ImagePipelineLowerer pass did not run, or ran but failed to lower this call.");

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result) =>
        ImageCostHelper.ComputeSupplementalCost(arguments);

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result, in InvocationFrame frame) =>
        ImageCostHelper.ComputeSupplementalCost(arguments, in frame);
}
