using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Image;

/// <summary>
/// <c>image_to_bytes(img Image) → Array&lt;UInt8&gt;</c>. Extracts raw RGBA pixel
/// bytes from an image and returns them as a flat <see cref="DataKind.UInt8"/>
/// array of length <c>H × W × 4</c> in row-major RGBA order. The image is
/// decoded (if not already a bitmap) and color-converted to
/// <see cref="SKColorType.Rgba8888"/> when its native color type differs.
/// </summary>
public sealed class ImageToBytesFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "image_to_bytes";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Extracts raw RGBA pixel bytes from an image as a flat Array<UInt8> "
        + "of length H x W x 4 in row-major RGBA order.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("image", DataKindMatcher.Exact(DataKind.Image)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.UInt8))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ImageToBytesFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef imgArg = arguments.Span[0];
        if (imgArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.UInt8));
        }

        SKBitmap source = imgArg.AsImage();
        byte[] pixels = ExtractRgbaPixels(source);
        return new ValueTask<ValueRef>(ValueRef.FromPrimitiveArray(pixels, DataKind.UInt8));
    }

    private static byte[] ExtractRgbaPixels(SKBitmap source)
    {
        SKBitmap rgba = source;
        SKBitmap? converted = null;
        if (source.ColorType != SKColorType.Rgba8888)
        {
            converted = new SKBitmap(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using (SKCanvas canvas = new(converted))
            {
                canvas.DrawBitmap(source, 0, 0);
            }
            rgba = converted;
        }

        try
        {
            int byteCount = rgba.Height * rgba.Width * 4;
            byte[] pixels = new byte[byteCount];
            ReadOnlySpan<byte> srcSpan = rgba.GetPixelSpan();
            srcSpan[..byteCount].CopyTo(pixels);
            return pixels;
        }
        finally
        {
            converted?.Dispose();
        }
    }
}
