using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using SkiaSharp;

namespace Heliosoph.DatumV.Functions.Scalar.Image;

/// <summary>
/// <c>perceptual_hash(img Image) → Float32[64]</c>. Difference hash (dHash):
/// resize the image to 9×8 BT.601 grayscale, then for each row compare
/// horizontally adjacent pixel pairs (8 pairs × 8 rows = 64 bits) — emitting
/// 1.0 when the left pixel is brighter than the right, 0.0 otherwise.
/// Pair with <c>hamming_distance()</c> for similarity comparison.
/// </summary>
public sealed class PerceptualHashFunction : IFunction, IScalarFunction
{
    private const int HashWidth = 9;
    private const int HashHeight = 8;
    private const int HashBitCount = (HashWidth - 1) * HashHeight; // = 64

    /// <inheritdoc />
    public static string Name => "perceptual_hash";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Difference hash (dHash) producing a 64-element Float32[] of 0/1 bits. "
        + "Use with hamming_distance() for similarity comparison.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("image", DataKindMatcher.Exact(DataKind.Image))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PerceptualHashFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef imgArg = arguments.Span[0];
        if (imgArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.Float32));
        }

        SKBitmap source = imgArg.AsImage();
        using SKBitmap? resized = source.Resize(
            new SKImageInfo(HashWidth, HashHeight), new SKSamplingOptions(SKFilterMode.Linear));
        if (resized is null)
        {
            throw new FunctionArgumentException(Name,
                $"failed to produce the {HashWidth}×{HashHeight} downsampled bitmap for hashing.");
        }

        SKBitmap rgba = ImagePixelAccess.AsRgba8888(resized, out SKBitmap? owned);
        try
        {
            ReadOnlySpan<byte> pixels = rgba.GetPixelSpan();
            float[] gray = new float[HashWidth * HashHeight];
            for (int i = 0; i < gray.Length; i++)
            {
                int o = i * 4;
                gray[i] = pixels[o] * ImagePixelAccess.Bt601RedWeight
                        + pixels[o + 1] * ImagePixelAccess.Bt601GreenWeight
                        + pixels[o + 2] * ImagePixelAccess.Bt601BlueWeight;
            }

            float[] hash = new float[HashBitCount];
            int bit = 0;
            for (int y = 0; y < HashHeight; y++)
            {
                int rowBase = y * HashWidth;
                for (int x = 0; x < HashWidth - 1; x++)
                {
                    hash[bit++] = gray[rowBase + x] > gray[rowBase + x + 1] ? 1f : 0f;
                }
            }

            return new ValueTask<ValueRef>(ValueRef.FromPrimitiveArray(hash, DataKind.Float32));
        }
        finally
        {
            owned?.Dispose();
        }
    }
}
