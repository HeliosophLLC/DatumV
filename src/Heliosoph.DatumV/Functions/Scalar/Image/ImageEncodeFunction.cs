using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions.Image;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using SkiaSharp;

namespace Heliosoph.DatumV.Functions.Scalar.Image;

/// <summary>
/// <c>image_encode(img Image, format String[, quality Int32]) → Array&lt;UInt8&gt;</c>.
/// Encodes an <see cref="DataKind.Image"/> as a byte array in the requested
/// container format. <c>format</c> is a case-insensitive string enum:
/// <c>'jpeg'</c> (alias <c>'jpg'</c>), <c>'png'</c>, or <c>'webp'</c>.
/// <c>quality</c> (default <c>90</c>) is the JPEG / WebP quality in the range
/// <c>0..100</c>; PNG ignores it (always lossless).
/// </summary>
/// <remarks>
/// The image is always decoded to a bitmap and re-encoded, even when the
/// requested format matches the source's existing container — there's no
/// fast-path byte-passthrough. Use this when you specifically want JPEG (e.g.
/// shrinking a large tiled preview) or WebP output; if you just want the raw
/// encoded bytes regardless of format, sidestep <c>image_encode</c> entirely
/// and consume the image bytes from the source.
/// </remarks>
public sealed class ImageEncodeFunction : IFunction, IScalarFunction
{
    private static readonly string[] FormatValues = ["jpeg", "jpg", "png", "webp"];

    /// <inheritdoc />
    public static string Name => "image_encode";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Encodes an Image as a byte array in the requested container format: "
        + "image_encode(img Image, format String[, quality Int32]) → Array<UInt8>. "
        + "format ∈ {'jpeg' (or 'jpg'), 'png', 'webp'} (case-insensitive). "
        + "quality is the JPEG/WebP quality 0..100 (default 90); PNG ignores it.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("image",   DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("format",  DataKindMatcher.StringEnum(FormatValues)),
                new ParameterSpec("quality", DataKindMatcher.Family(DataKindFamily.IntegerFamily),
                    IsArray: ArrayMatch.Scalar, IsOptional: true),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.UInt8))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (2 or 3))
        {
            throw new ArgumentException(
                "image_encode() requires 2 or 3 arguments: image, format[, quality].");
        }
        if (argumentKinds[0] != DataKind.Image)
        {
            throw new ArgumentException(
                $"image_encode() first argument must be Image, got {argumentKinds[0]}.");
        }
        if (argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException(
                $"image_encode() second argument (format) must be String, got {argumentKinds[1]}.");
        }
        if (argumentKinds.Length == 3 && !IsIntegerKind(argumentKinds[2]))
        {
            throw new ArgumentException(
                $"image_encode() third argument (quality) must be an integer, got {argumentKinds[2]}.");
        }
        return DataKind.UInt8;
    }

    private static bool IsIntegerKind(DataKind kind) =>
        kind is DataKind.Int8 or DataKind.Int16 or DataKind.Int32 or DataKind.Int64;

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.UInt8));
        }

        string formatString = args[1].AsString();
        SKEncodedImageFormat format = ImageEncoder.ParseFormatString(formatString);

        int quality = 90;
        if (args.Length == 3 && !args[2].IsNull)
        {
            quality = args[2].ToInt32();
            if (quality < 0 || quality > 100)
            {
                throw new FunctionArgumentException(Name,
                    $"quality must be in 0..100, got {quality}.");
            }
        }

        SKBitmap bitmap = args[0].AsImage();
        byte[] encoded = ImageEncoder.Encode(bitmap, format, quality);
        return new ValueTask<ValueRef>(ValueRef.FromPrimitiveArray(encoded, DataKind.UInt8));
    }
}
