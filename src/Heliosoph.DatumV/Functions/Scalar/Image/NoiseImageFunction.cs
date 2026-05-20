using System.Runtime.InteropServices;

using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using SkiaSharp;

namespace Heliosoph.DatumV.Functions.Scalar.Image;

/// <summary>
/// <c>noise(img Image, value) → Image</c> — additive Gaussian noise (the
/// two-arg form defaults to <c>'gaussian'</c>).
/// <c>noise(img Image, type String, value) → Image</c> — explicit noise model.
/// Supported types:
/// <list type="bullet">
///   <item><c>'gaussian'</c> — adds zero-mean Gaussian samples (Box–Muller)
///   with standard deviation <c>value</c> (in 0–255 byte units) to each RGB
///   channel; alpha untouched.</item>
///   <item><c>'salt_pepper'</c> — sets <c>value</c> fraction of pixels to
///   pure black or pure white, chosen uniformly per affected pixel.</item>
/// </list>
/// Marked non-pure: each invocation draws fresh randomness, so CSE does not
/// collapse repeated calls.
/// </summary>
public sealed class NoiseImageFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "noise";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Adds noise to an image. Two-arg form defaults to Gaussian; three-arg form takes "
        + "an explicit type 'gaussian' (val=stddev, 0–255) or 'salt_pepper' (val=ratio).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("image", DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("value", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("image", DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("type",  DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("value", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public bool IsPure => false;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<NoiseImageFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef imgArg = args[0];
        if (imgArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
        }

        string type;
        float value;
        if (args.Length == 2)
        {
            type = "gaussian";
            if (args[1].IsNull) return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
            value = args[1].ToFloat();
        }
        else
        {
            if (args[1].IsNull || args[2].IsNull) return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
            type = args[1].AsString().ToLowerInvariant();
            value = args[2].ToFloat();
        }

        SKBitmap source = imgArg.AsImage();
        SKBitmap rgba = ImagePixelAccess.AsRgba8888(source, out SKBitmap? owned);
        try
        {
            int width = rgba.Width;
            int height = rgba.Height;
            ReadOnlySpan<byte> src = rgba.GetPixelSpan();
            byte[] outBytes = new byte[width * height * 4];
            src.CopyTo(outBytes);

            switch (type)
            {
                case "gaussian":
                    ApplyGaussian(outBytes, value);
                    break;
                case "salt_pepper":
                    ApplySaltPepper(outBytes, value);
                    break;
                default:
                    throw new FunctionArgumentException(Name,
                        $"unknown noise type '{type}'. Supported: gaussian, salt_pepper.");
            }

            SKBitmap result = new(new SKImageInfo(width, height, SKColorType.Rgba8888,
                                                  rgba.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Unpremul));
            Marshal.Copy(outBytes, 0, result.GetPixels(), outBytes.Length);
            return new ValueTask<ValueRef>(ValueRef.FromImage(result));
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private static void ApplyGaussian(byte[] pixels, float stddev)
    {
        Random rng = Random.Shared;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            // Box–Muller per pixel; the same sample is added to R, G, B for cheap
            // luminance-style noise (matches the deleted implementation).
            double u1 = 1.0 - rng.NextDouble();
            double u2 = rng.NextDouble();
            double n = System.Math.Sqrt(-2.0 * System.Math.Log(u1))
                     * System.Math.Sin(2.0 * System.Math.PI * u2);
            float delta = (float)(n * stddev);
            pixels[i]     = ClampByte(pixels[i] + delta);
            pixels[i + 1] = ClampByte(pixels[i + 1] + delta);
            pixels[i + 2] = ClampByte(pixels[i + 2] + delta);
        }
    }

    private static void ApplySaltPepper(byte[] pixels, float ratio)
    {
        Random rng = Random.Shared;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (rng.NextDouble() >= ratio) continue;
            byte v = rng.NextDouble() < 0.5 ? (byte)0 : (byte)255;
            pixels[i]     = v;
            pixels[i + 1] = v;
            pixels[i + 2] = v;
        }
    }

    private static byte ClampByte(float v) =>
        v <= 0f ? (byte)0 : v >= 255f ? (byte)255 : (byte)v;
}
