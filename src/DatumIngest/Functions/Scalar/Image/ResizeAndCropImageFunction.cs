using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Image;

/// <summary>
/// <c>resize_and_crop(img Image, w, h, gravity String) → Image</c>. Scales
/// the image to fill the target rectangle (preserves aspect ratio, picks
/// the larger of the X/Y scale factors), then crops the excess from the
/// requested gravity anchor. Gravity values: <c>'center'</c>, <c>'top'</c>,
/// <c>'bottom'</c>, <c>'left'</c>, <c>'right'</c> (case-insensitive).
/// </summary>
public sealed class ResizeAndCropImageFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "resize_and_crop";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Resize to fill then crop to the exact target size. Gravity selects which excess to discard.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("image",   DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("width",   DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("height",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("gravity", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public int QueryUnitCost => 50;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ResizeAndCropImageFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull || args[3].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
        }

        int targetWidth = args[1].ToInt32();
        int targetHeight = args[2].ToInt32();
        if (targetWidth <= 0 || targetHeight <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"width and height must be positive; got {targetWidth}×{targetHeight}.");
        }
        string gravity = args[3].AsString().ToLowerInvariant();

        SKBitmap source = args[0].AsImage();
        float scale = System.Math.Max(
            (float)targetWidth / source.Width,
            (float)targetHeight / source.Height);
        int scaledWidth = (int)System.Math.Ceiling(source.Width * scale);
        int scaledHeight = (int)System.Math.Ceiling(source.Height * scale);

        using SKBitmap? resized = source.Resize(
            new SKImageInfo(scaledWidth, scaledHeight), SKSamplingOptions.Default);
        if (resized is null)
        {
            throw new FunctionArgumentException(Name,
                $"failed to produce the {scaledWidth}×{scaledHeight} intermediate bitmap.");
        }

        (int cropX, int cropY) = ComputeCropOffset(
            scaledWidth, scaledHeight, targetWidth, targetHeight, gravity);

        SKBitmap output = new(targetWidth, targetHeight);
        using (SKCanvas canvas = new(output))
        {
            canvas.DrawBitmap(
                resized,
                new SKRect(cropX, cropY, cropX + targetWidth, cropY + targetHeight),
                new SKRect(0, 0, targetWidth, targetHeight));
        }
        return new ValueTask<ValueRef>(ValueRef.FromImage(output));
    }

    private static (int X, int Y) ComputeCropOffset(
        int sourceWidth, int sourceHeight, int targetWidth, int targetHeight, string gravity)
    {
        int excessX = sourceWidth - targetWidth;
        int excessY = sourceHeight - targetHeight;
        return gravity switch
        {
            "center" => (excessX / 2, excessY / 2),
            "top"    => (excessX / 2, 0),
            "bottom" => (excessX / 2, excessY),
            "left"   => (0, excessY / 2),
            "right"  => (excessX, excessY / 2),
            _ => throw new FunctionArgumentException(Name,
                $"unknown gravity '{gravity}'. Supported: center, top, bottom, left, right."),
        };
    }
}
