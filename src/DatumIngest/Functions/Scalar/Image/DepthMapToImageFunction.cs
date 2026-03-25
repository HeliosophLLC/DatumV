using DatumIngest.Execution;
using DatumIngest.Functions.Scalar.Activation;
using DatumIngest.Manifest;
using DatumIngest.Model;

using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Image;

/// <summary>
/// <c>depth_map_to_image(values FLOAT32[], source_h INT, source_w INT,
/// target_h INT, target_w INT [, invert BOOL]) → Image</c>. Packs a
/// single-channel Float32 grid into a grayscale image with per-image
/// min-max normalisation and bilinear resize to the requested output
/// dimensions. The optional <c>invert</c> flag flips the brightness
/// mapping so callers can normalise the bright-vs-dark convention
/// across the inverse-depth and metric-depth model families.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this is for.</strong> Monocular depth estimators (MiDaS,
/// DPT) and segmentation models (U²-Net, BiSeNet) emit a flat Float32
/// array of inverse depth / mask probability in network-resolution
/// space. Visualising it requires three steps: rescale to [0, 1],
/// pack into a viewable image, and resize back to the original image's
/// dimensions. Bundling those into one function keeps the SQL body for
/// any single-channel-output model a one-liner.
/// </para>
/// <para>
/// <strong>Math.</strong> Per-image min-max normalisation maps the input's
/// observed range to [0, 1] — bigger value = brighter pixel. MiDaS and
/// DPT emit *inverse* depth in arbitrary units (bigger = closer), so the
/// default mapping reads as "near = bright" for that family. Metric depth
/// models (ZoeDepth, GLPN-NYU) emit *real* meters where bigger = farther;
/// passing <c>invert => true</c> on those bodies inverts the post-normalise
/// brightness to <c>1.0 - normalised</c>, keeping near = bright across
/// the catalog so model-vs-model visual comparison is consistent. The
/// output is grayscale-as-RGBA (R=G=B=normalised value, A=255) to stay
/// uniform with every other image-emitting model. PNG encoding happens
/// at the <see cref="ValueRef.FromImage(SKBitmap)"/> boundary.
/// </para>
/// <para>
/// <strong>Resize.</strong> Bilinear from the source tensor's
/// <c>(source_h, source_w)</c> grid to the requested
/// <c>(target_h, target_w)</c> pixel dimensions. Pass the original
/// image's dimensions to recover a depth map aligned with the source.
/// </para>
/// </remarks>
public sealed class DepthMapToImageFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "depth_map_to_image";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Packs a single-channel Float32 grid into a grayscale image with per-image min-max "
        + "normalisation and bilinear resize: depth_map_to_image(values FLOAT32[], source_h, "
        + "source_w, target_h, target_w [, invert BOOL]) → Image. Use for MiDaS / DPT / U²-Net "
        + "outputs (default mapping is bigger value = brighter pixel). Pass invert => true on "
        + "metric depth models (ZoeDepth, GLPN-NYU) so near = bright matches the inverse-depth "
        + "family convention.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("values",   DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array,
                    Metadata: new ParameterMetadata(
                        Description: "Flat Float32 grid of inverse-depth or mask values, row-major at (source_h × source_w).")),
                new ParameterSpec("source_h", DataKindMatcher.Family(DataKindFamily.IntegerFamily),
                    Metadata: new ParameterMetadata(
                        Check: new GreaterThanCheck(0m),
                        Unit: "pixels",
                        Description: "Source grid height — must equal the network's output resolution.")),
                new ParameterSpec("source_w", DataKindMatcher.Family(DataKindFamily.IntegerFamily),
                    Metadata: new ParameterMetadata(
                        Check: new GreaterThanCheck(0m),
                        Unit: "pixels",
                        Description: "Source grid width — must equal the network's output resolution.")),
                new ParameterSpec("target_h", DataKindMatcher.Family(DataKindFamily.IntegerFamily),
                    Metadata: new ParameterMetadata(
                        Check: new GreaterThanCheck(0m),
                        Unit: "pixels",
                        Description: "Output image height after bilinear resize. Pass the original image's height to align.")),
                new ParameterSpec("target_w", DataKindMatcher.Family(DataKindFamily.IntegerFamily),
                    Metadata: new ParameterMetadata(
                        Check: new GreaterThanCheck(0m),
                        Unit: "pixels",
                        Description: "Output image width after bilinear resize. Pass the original image's width to align.")),
                new ParameterSpec("invert", DataKindMatcher.Exact(DataKind.Boolean), IsOptional: true,
                    Metadata: new ParameterMetadata(
                        Description: "When true, the post-normalise brightness is flipped to (1 - normalised). "
                            + "Use on metric-depth models (ZoeDepth, GLPN-NYU) where bigger value = farther, "
                            + "so that 'near = bright' matches the default inverse-depth convention. "
                            + "Defaults to false; existing inverse-depth bodies (MiDaS, DPT, Depth-Anything) "
                            + "don't need to pass anything.")),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DepthMapToImageFunction>(argumentKinds);

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

        float[] values = ActivationOps.ReadFloat32Array(args[0]);
        int sourceH = ReadIntArg(args[1], "source_h");
        int sourceW = ReadIntArg(args[2], "source_w");
        int targetH = ReadIntArg(args[3], "target_h");
        int targetW = ReadIntArg(args[4], "target_w");
        // Optional invert flag — when present and true, flip the
        // post-normalise brightness so callers can keep "near = bright"
        // visually consistent across inverse-depth and metric-depth
        // model families.
        bool invert = args.Length >= 6 && !args[5].IsNull && args[5].AsBoolean();

        int planeSize = sourceH * sourceW;
        if (values.Length != planeSize)
        {
            throw new FunctionArgumentException(Name,
                $"values length {values.Length} doesn't match source_h × source_w = {sourceH} × {sourceW} = {planeSize}.");
        }

        // Per-image min-max normalisation. Inverse depth / mask
        // probabilities arrive in arbitrary units; without this the
        // visible image is washed out and per-pixel dynamic range is
        // meaningless.
        float min = float.PositiveInfinity;
        float max = float.NegativeInfinity;
        for (int i = 0; i < planeSize; i++)
        {
            float v = values[i];
            if (v < min) min = v;
            if (v > max) max = v;
        }
        // Degenerate uniform grid — avoid divide-by-zero, emit flat black.
        float range = (max - min) > 1e-6f ? (max - min) : 1f;

        // Pack into a grayscale-as-RGBA bitmap at source resolution.
        SKImageInfo smallInfo = new(sourceW, sourceH, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using SKBitmap small = new(smallInfo);
        nint smallPtr = small.GetPixels();
        unsafe
        {
            byte* dst = (byte*)smallPtr;
            for (int i = 0; i < planeSize; i++)
            {
                float v = (values[i] - min) / range;
                if (invert) v = 1f - v;
                byte g = ToByte(v);
                int o = i * 4;
                dst[o + 0] = g;
                dst[o + 1] = g;
                dst[o + 2] = g;
                dst[o + 3] = 255;
            }
        }

        // Resize back to target dimensions. Identity resize when sizes
        // match — Skia handles that as a no-op copy.
        SKImageInfo finalInfo = new(targetW, targetH, SKColorType.Rgba8888, SKAlphaType.Opaque);
        SKBitmap final = small.Resize(finalInfo, SKSamplingOptions.Default)
            ?? throw new InvalidOperationException(
                $"depth_map_to_image: SkiaSharp failed to resize to {targetW}×{targetH}.");

        return new ValueTask<ValueRef>(ValueRef.FromImage(final));
    }

    private static byte ToByte(float v)
    {
        float scaled = v * 255f;
        if (scaled < 0f) return 0;
        if (scaled > 255f) return 255;
        return (byte)MathF.Round(scaled);
    }

    private static int ReadIntArg(ValueRef arg, string name)
    {
        int value = arg.Kind switch
        {
            DataKind.Int8 => arg.AsInt8(),
            DataKind.Int16 => arg.AsInt16(),
            DataKind.Int32 => arg.AsInt32(),
            DataKind.Int64 => checked((int)arg.AsInt64()),
            _ => throw new FunctionArgumentException("depth_map_to_image",
                $"{name} must be an integer kind, got {arg.Kind}."),
        };
        if (value <= 0)
        {
            throw new FunctionArgumentException("depth_map_to_image",
                $"{name} must be > 0, got {value}.");
        }
        return value;
    }
}
