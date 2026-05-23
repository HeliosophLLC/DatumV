using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using SkiaSharp;

namespace Heliosoph.DatumV.Functions.Scalar.Image;

/// <summary>
/// <c>resize(img Image, width, height [, mode]) → Image</c>. Resizes an
/// image to the requested pixel dimensions. Width and height must be
/// positive integers; float arguments truncate. Optional <c>mode</c>
/// selects the sampling filter; defaults to <c>'bilinear'</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Supported modes</strong> (case-insensitive):
/// <list type="bullet">
/// <item><c>nearest</c> — nearest-neighbour. Hard edges, no blending.
///   Useful for pixel-art upscales and for resizing label/index maps
///   where colour interpolation would invent new classes.</item>
/// <item><c>bilinear</c> — 2×2 linear sampling. Default. Matches the
///   OpenCV / torchvision / TensorFlow defaults used by most CV
///   preprocessing pipelines.</item>
/// <item><c>trilinear</c> — bilinear plus linear mipmap blending.
///   Helpful when downscaling by more than ~2×; closer to OpenCV's
///   <c>INTER_AREA</c> behaviour for large reductions.</item>
/// <item><c>mitchell</c> — Mitchell–Netravali cubic (B=1/3, C=1/3).
///   Balanced sharpness and ringing; a general-purpose photographic
///   default when bilinear looks too soft.</item>
/// <item><c>catmullrom</c> — Catmull–Rom cubic (B=0, C=0.5). Sharper
///   than Mitchell at the cost of more visible ringing on hard edges.</item>
/// </list>
/// SkiaSharp does not ship a Lanczos filter; the cubic resamplers are
/// the highest-quality option available.
/// </para>
/// </remarks>
public sealed class ResizeImageFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "resize";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Resizes an image to the requested pixel dimensions: "
        + "resize(img, width, height [, mode]). Width and height must be positive; "
        + "float arguments truncate to integers. Mode selects the sampling filter "
        + "('nearest', 'bilinear' (default), 'trilinear', 'mitchell', 'catmullrom').";

    /// <summary>
    /// Sampling-mode name → SkiaSharp <c>SKSamplingOptions</c> table. Names
    /// are matched case-insensitively. Adding a mode is a one-line addition
    /// here.
    /// </summary>
    private static readonly Dictionary<string, SKSamplingOptions> SamplingModes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["nearest"]    = new SKSamplingOptions(SKFilterMode.Nearest),
            ["bilinear"]   = new SKSamplingOptions(SKFilterMode.Linear),
            ["trilinear"]  = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
            ["mitchell"]   = new SKSamplingOptions(SKCubicResampler.Mitchell),
            ["catmullrom"] = new SKSamplingOptions(SKCubicResampler.CatmullRom),
        };

    /// <summary>
    /// Stable, sorted snapshot of the supported mode names. Surfaced to the
    /// language server via <see cref="StringEnumMatcher"/> and used by the
    /// unknown-mode error message.
    /// </summary>
    public static IReadOnlyList<string> AvailableModes { get; } =
        SamplingModes.Keys.OrderBy(s => s, StringComparer.Ordinal).ToArray();

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("image",  DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("width",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("height", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("mode",   DataKindMatcher.StringEnum(AvailableModes),
                    IsOptional: true),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ResizeImageFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef imgArg = args[0];
        if (imgArg.IsNull || args[1].IsNull || args[2].IsNull)
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

        SKSamplingOptions sampling = new(SKFilterMode.Linear);
        if (args.Length == 4)
        {
            ValueRef modeArg = args[3];
            if (modeArg.IsNull)
            {
                return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
            }
            string modeName = modeArg.AsString();
            if (!SamplingModes.TryGetValue(modeName, out sampling))
            {
                throw new FunctionArgumentException(Name,
                    $"unknown sampling mode '{modeName}'. Supported modes: "
                    + string.Join(", ", AvailableModes) + ".");
            }
        }

        SKBitmap source = imgArg.AsImage();
        SKBitmap? resized = source.Resize(
            new SKImageInfo(targetWidth, targetHeight), sampling);
        if (resized is null)
        {
            throw new FunctionArgumentException(Name,
                $"failed to produce a {targetWidth}×{targetHeight} bitmap.");
        }
        return new ValueTask<ValueRef>(ValueRef.FromImage(resized));
    }
}
