using DatumIngest.Execution;
using DatumIngest.Functions.Scalar.Activation;
using DatumIngest.Manifest;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Image;

/// <summary>
/// Inverse of <see cref="ImageToTensorFunction"/>: takes a flat NCHW
/// Float32 tensor (3 channels × <c>height</c> × <c>width</c>), optionally
/// denormalises with <c>mean</c> / <c>std</c>, and packs the bytes back
/// into a PNG-encoded RGB image.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Call shapes.</strong>
/// <list type="bullet">
///   <item>
///     <c>tensor_to_image(tensor, height, width)</c> — 3-arg shortcut.
///     Assumes the tensor is already in [0, 1] range (i.e. produced by
///     <c>image_to_tensor</c> with default mean=[0,0,0] std=[1,1,1]).
///     Multiplied by 255 and clamped to byte range.
///   </item>
///   <item>
///     <c>tensor_to_image(tensor, height, width, mean, std)</c> — full
///     form. Applies the inverse normalize <c>(value * std + mean) * 255</c>
///     before clamping. Use the same mean/std the producer used to encode.
///   </item>
/// </list>
/// </para>
/// <para>
/// <strong>Output.</strong> RGB (alpha = 255), PNG-encoded. Float values
/// outside [0, 1] post-denormalize clamp to byte range — diffusion-model
/// outputs frequently land slightly outside the range and we don't want
/// the rounding to silently wrap.
/// </para>
/// </remarks>
public sealed class TensorToImageFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "tensor_to_image";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Packs a flat NCHW Float32 tensor back into a PNG-encoded RGB image: " +
        "tensor_to_image(tensor FLOAT32[], height INT, width INT [, mean FLOAT32[3], std FLOAT32[3]]). " +
        "5-arg form applies inverse normalize (value * std + mean); 3-arg form skips it. " +
        "Tensor must have length 3 × height × width.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("tensor", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("height", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("width",  DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("mean",   DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("std",    DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
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
        FunctionMetadata.Validate<TensorToImageFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull)
        {
            return new(ValueRef.Null(DataKind.Image));
        }

        int height = args[1].ToInt32();
        int width = args[2].ToInt32();
        if (height <= 0 || width <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"height and width must be positive, got [{height}, {width}].");
        }

        float meanR = 0f, meanG = 0f, meanB = 0f;
        float stdR = 1f, stdG = 1f, stdB = 1f;
        if (args.Length == 5)
        {
            (meanR, meanG, meanB) = ReadFloat3(args[3], "mean");
            (stdR, stdG, stdB)    = ReadFloat3(args[4], "std");
        }

        float[] tensor = ActivationOps.ReadFloat32Array(args[0]);
        int plane = height * width;
        int expected = 3 * plane;
        if (tensor.Length != expected)
        {
            throw new FunctionArgumentException(Name,
                $"tensor length must equal 3 × height × width = {expected}, got {tensor.Length}.");
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
                    float r = tensor[0 * plane + idx] * stdR + meanR;
                    float g = tensor[1 * plane + idx] * stdG + meanG;
                    float b = tensor[2 * plane + idx] * stdB + meanB;

                    int dst = idx * 4;
                    p[dst + 0] = ToByte(r);
                    p[dst + 1] = ToByte(g);
                    p[dst + 2] = ToByte(b);
                    p[dst + 3] = 255;
                }
            }
        }

        return new(ValueRef.FromImage(output));
    }

    /// <summary>
    /// Clamps a [0, 1]-range float to a [0, 255] byte. Values outside the
    /// expected range still produce sensible bytes — diffusion outputs
    /// routinely drift slightly past the bounds and silent wraparound
    /// would tile output images with wrong-coloured pixels.
    /// </summary>
    private static byte ToByte(float v)
    {
        float scaled = v * 255f;
        if (scaled <= 0f) return 0;
        if (scaled >= 255f) return 255;
        return (byte)(scaled + 0.5f);
    }

    /// <summary>
    /// Reads a 3-element Float32 array as (R, G, B). Same dual-payload
    /// handling as <see cref="ImageToTensorFunction"/>: accepts both the
    /// <c>FromPrimitiveArray</c> typed buffer and the <c>ValueRef[]</c>
    /// inline-array form.
    /// </summary>
    private static (float R, float G, float B) ReadFloat3(ValueRef arg, string paramName)
    {
        if (arg.IsNull)
        {
            throw new FunctionArgumentException(Name, $"{paramName} must not be null.");
        }
        if (arg.Materialized is float[] direct)
        {
            if (direct.Length != 3)
            {
                throw new FunctionArgumentException(Name,
                    $"{paramName} must have exactly 3 elements (R, G, B), got {direct.Length}.");
            }
            return (direct[0], direct[1], direct[2]);
        }
        ReadOnlySpan<ValueRef> elements = arg.GetArrayElements();
        if (elements.Length != 3)
        {
            throw new FunctionArgumentException(Name,
                $"{paramName} must have exactly 3 elements (R, G, B), got {elements.Length}.");
        }
        if (!elements[0].TryToFloat(out float r) ||
            !elements[1].TryToFloat(out float g) ||
            !elements[2].TryToFloat(out float b))
        {
            throw new FunctionArgumentException(Name,
                $"{paramName} elements must be coercible to Float32.");
        }
        return (r, g, b);
    }
}
