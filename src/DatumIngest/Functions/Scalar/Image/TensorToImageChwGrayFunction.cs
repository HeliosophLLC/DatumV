using DatumIngest.Execution;
using DatumIngest.Functions.Scalar.Activation;
using DatumIngest.Manifest;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Image;

/// <summary>
/// Grayscale sibling of <see cref="TensorToImageChwFunction"/>. Takes a
/// flat single-channel NCHW Float32 tensor (<c>height × width</c> values,
/// not <c>3 × height × width</c>) and packs it into an RGB image with
/// R = G = B = luma. The denormalised value goes to all three channels
/// simultaneously, so the output is a true grayscale render usable by
/// every downstream image viewer + every Image-consuming SQL function
/// without further conversion.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Use cases.</strong> Pair with <c>image_to_tensor_chw_gray</c>
/// for single-channel denoise / restoration pipelines (SCUNet-gray,
/// custom medical / document models). The <c>scunet_gray_*</c> SQL bodies
/// are the canonical caller.
/// </para>
/// <para>
/// <strong>Call shapes.</strong> Mirror the color sibling but with scalar
/// (not array) mean/std since there's only one channel:
/// <list type="bullet">
///   <item>
///     <c>tensor_to_image_chw_gray(tensor, height, width)</c> — 3-arg
///     shortcut. Assumes input is already in [0, 1]; multiplied by 255
///     and clamped to byte range.
///   </item>
///   <item>
///     <c>tensor_to_image_chw_gray(tensor, height, width, mean, std)</c>
///     — applies inverse normalize <c>(value * std + mean) * 255</c>
///     before clamping. Use the same mean/std the producer encoded with.
///   </item>
/// </list>
/// </para>
/// </remarks>
public sealed class TensorToImageChwGrayFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "tensor_to_image_chw_gray";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Single-channel sibling of tensor_to_image_chw. Packs a flat luma " +
        "(height × width) Float32 tensor into an RGB image with R = G = B = luma. " +
        "Call shapes: tensor_to_image_chw_gray(tensor, h, w [, mean, std]); mean/std " +
        "are scalars (not arrays). Pair with image_to_tensor_chw_gray for SCUNet-gray " +
        "and other single-channel restoration pipelines.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("tensor", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("height", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("width",  DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("mean",   DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("std",    DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("tensor", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("height", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("width",  DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<TensorToImageChwGrayFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
        }

        int height = args[1].ToInt32();
        int width = args[2].ToInt32();
        if (height <= 0 || width <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"height and width must be positive, got [{height}, {width}].");
        }

        float mean = 0f;
        float std = 1f;
        if (args.Length == 5)
        {
            mean = args[3].AsFloat32();
            std = args[4].AsFloat32();
        }

        float[] tensor = ActivationOps.ReadFloat32Array(args[0]);
        int plane = height * width;
        if (tensor.Length != plane)
        {
            throw new FunctionArgumentException(Name,
                $"tensor length must equal height × width = {plane} (single channel), got {tensor.Length}.");
        }

        SKImageInfo info = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        SKBitmap output = new(info);
        nint pixelPtr = output.GetPixels();

        unsafe
        {
            byte* p = (byte*)pixelPtr;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = y * width + x;
                    float v = tensor[idx] * std + mean;
                    byte g = ToByte(v);
                    int dst = idx * 4;
                    p[dst + 0] = g;
                    p[dst + 1] = g;
                    p[dst + 2] = g;
                    p[dst + 3] = 255;
                }
            }
        }

        return new ValueTask<ValueRef>(ValueRef.FromImage(output));
    }

    private static byte ToByte(float v)
    {
        float scaled = v * 255f;
        if (scaled < 0f) return 0;
        if (scaled > 255f) return 255;
        return (byte)MathF.Round(scaled);
    }
}
