namespace DatumIngest.Functions.Image;

using DatumIngest.Functions;
using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Computes a perceptual hash (dHash) of an image.
/// <c>perceptual_hash(img)</c> returns a 64-element vector of 0.0 and 1.0 values
/// representing the hash bits. Two similar images will produce similar hash vectors;
/// use <c>hamming_distance()</c> to compare them.
/// </summary>
/// <remarks>
/// Uses the difference hash (dHash) algorithm: resize to 9×8 grayscale,
/// compare horizontally adjacent pixels to produce a 64-bit hash.
/// </remarks>
public sealed class PerceptualHashFunction : IScalarFunction, ICostAwareFunction, IImagePipelineSink
{
    private const int HashWidth = 9;
    private const int HashHeight = 8;
    private const int HashBitCount = 64; // (HashWidth - 1) × HashHeight

    // ITU-R BT.601 luminance weights
    private const float RedWeight = 0.2126f;
    private const float GreenWeight = 0.7152f;
    private const float BlueWeight = 0.0722f;

    /// <inheritdoc />
    public string Name => "perceptual_hash";

    /// <inheritdoc />
    public int QueryUnitCost => 10;

    /// <inheritdoc cref="IImagePipelineSink.ResultKind" />
    public DataKind ResultKind => DataKind.Vector;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("perceptual_hash() requires exactly 1 argument.");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException(
                $"perceptual_hash() requires Image or UInt8Array, got {argumentKinds[0]}.");
        }

        return DataKind.Vector;
    }

    /// <inheritdoc />
    public void ValidateAuxiliaryArguments(ReadOnlySpan<DataKind> auxiliaryKinds)
    {
        if (auxiliaryKinds.Length != 0)
        {
            throw new ArgumentException(
                $"perceptual_hash() takes no auxiliary arguments in pipeline form; got {auxiliaryKinds.Length}.");
        }
    }

    /// <inheritdoc cref="IImagePipelineSink.Reduce" />
    public DataValue Reduce(SKBitmap input, ReadOnlySpan<DataValue> auxiliaryArgs, IValueStore targetStore)
    {
        using SKBitmap resized = input.Resize(
            new SKImageInfo(HashWidth, HashHeight), SKSamplingOptions.Default)
            ?? throw new InvalidOperationException("perceptual_hash() failed to resize image for hashing.");

        using SKBitmap rgba = resized.ColorType == SKColorType.Rgba8888
            ? resized
            : ConvertToRgba8888(resized);

        nint pixelPointer = rgba.GetPixels();

        float[] grayscale = new float[HashWidth * HashHeight];

        unsafe
        {
            byte* pixels = (byte*)pixelPointer;

            for (int i = 0; i < HashWidth * HashHeight; i++)
            {
                int offset = i * 4;
                grayscale[i] = pixels[offset] * RedWeight
                             + pixels[offset + 1] * GreenWeight
                             + pixels[offset + 2] * BlueWeight;
            }
        }

        float[] hash = new float[HashBitCount];
        int bitIndex = 0;

        for (int y = 0; y < HashHeight; y++)
        {
            for (int x = 0; x < HashWidth - 1; x++)
            {
                float left = grayscale[y * HashWidth + x];
                float right = grayscale[y * HashWidth + x + 1];
                hash[bitIndex] = left > right ? 1.0f : 0.0f;
                bitIndex++;
            }
        }

        return DataValue.FromVector(hash, targetStore);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments) =>
        throw new InvalidOperationException(
            "perceptual_hash() must be lowered to a FusedImagePipelineExpression at plan time " +
            "and should never reach the runtime evaluator. This indicates the " +
            "ImagePipelineLowerer pass did not run, or ran but failed to lower this call.");

    private static SKBitmap ConvertToRgba8888(SKBitmap source)
    {
        SKBitmap converted = new(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKCanvas canvas = new(converted);
        canvas.DrawBitmap(source, 0, 0);
        return converted;
    }

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result) =>
        ImageCostHelper.ComputeSupplementalCost(arguments);

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result, in InvocationFrame frame) =>
        ImageCostHelper.ComputeSupplementalCost(arguments, in frame);
}
