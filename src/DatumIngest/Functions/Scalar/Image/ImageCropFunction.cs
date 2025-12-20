using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Image;

/// <summary>
/// Crops a rectangular region out of an image.
/// <c>image_crop(image, x, y, w, h)</c> — all numeric kinds accepted for the
/// coordinate arguments (float values truncate to integer pixel offsets).
/// Returns a null Image when any argument is null.
/// </summary>
public sealed class ImageCropFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "image_crop";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Crops a rectangular region from an image. "
        + "Coordinates are in pixels; float values truncate to integers.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("image", DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("x",     DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("y",     DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("w",     DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("h",     DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ImageCropFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueRef Execute(ReadOnlySpan<ValueRef> arguments, in EvaluationFrame frame)
    {
        ValueRef imgArg = arguments[0];
        ValueRef xArg   = arguments[1];
        ValueRef yArg   = arguments[2];
        ValueRef wArg   = arguments[3];
        ValueRef hArg   = arguments[4];

        if (imgArg.IsNull || xArg.IsNull || yArg.IsNull || wArg.IsNull || hArg.IsNull)
        {
            return ValueRef.Null(DataKind.Image);
        }

        SKBitmap source = imgArg.AsImage();
        int x = xArg.ToInt32();
        int y = yArg.ToInt32();
        int w = wArg.ToInt32();
        int h = hArg.ToInt32();

        if (w <= 0 || h <= 0)
        {
            throw new ArgumentOutOfRangeException(
                $"image_crop: width and height must be positive, got w={w}, h={h}.");
        }

        var rect = new SKRectI(x, y, x + w, y + h);
        using var subset = new SKBitmap();
        if (!source.ExtractSubset(subset, rect))
        {
            throw new InvalidOperationException(
                $"image_crop: region ({x},{y},{w},{h}) falls outside the {source.Width}×{source.Height} image.");
        }

        // subset shares pixel memory with source — Copy() produces an owned bitmap.
        return ValueRef.FromImage(subset.Copy());
    }
}
