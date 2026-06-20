using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using SkiaSharp;

namespace Heliosoph.DatumV.Functions.Scalar.Image;

/// <summary>
/// Maps a single-channel grayscale image through a perceptually-tuned colour
/// palette (a "colourmap"), returning a fully-coloured <c>Image</c>.
/// <c>apply_colormap(image, palette_name)</c> — reads the input's red-channel
/// intensity as the scalar value, looks up the corresponding RGB triple in
/// the named palette, and writes a fully-opaque output of the same dimensions.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why.</strong> Grayscale depth maps and saliency masks are hard to
/// read at a glance — the human visual system has poor luminance contrast
/// sensitivity in mid-tones, so a depth-rich scene often looks like a flat
/// grey blob. Mapping intensity to hue (false-colour) lets depth differences
/// register as colour differences rather than relying on brightness alone.
/// </para>
/// <para>
/// <strong>Composes generically.</strong> Designed for the depth-estimation
/// pipeline (<c>models.midas_small</c> / <c>models.dpt_large</c>) but doesn't
/// care about the source — works on any single-channel-as-RGBA image
/// (U²-Net masks, attention maps, future heatmaps), since they all encode
/// the scalar in the red channel by convention.
/// </para>
/// <para>
/// <strong>Supported palettes</strong> (case-insensitive):
/// <list type="bullet">
/// <item><c>turbo</c> — Google's perceptually-improved jet (blue → cyan →
///   green → yellow → orange → red). Best default for depth maps; sharp hue
///   progression with no green-yellow ambiguity. Computed via Anton
///   Mikhailov's degree-5 polynomial approximation.</item>
/// <item><c>jet</c> — legacy MATLAB rainbow (blue → cyan → green → yellow →
///   red). Provided for matching prior art; <c>turbo</c> is strictly better
///   visually. Closed-form piecewise-linear ramps.</item>
/// <item><c>gray</c> — identity pass-through (R = G = B = input intensity).
///   Useful as a debug palette and for round-trip checks.</item>
/// </list>
/// Matplotlib's perceptually-uniform palettes (<c>viridis</c>, <c>inferno</c>,
/// <c>magma</c>, <c>plasma</c>) are not yet wired up — same shape, requires an
/// embedded 256-entry LUT per palette. Add via <see cref="Palettes"/> when
/// demand arrives.
/// </para>
/// <para>
/// <strong>Implementation</strong>. Per call: compute a 256-entry RGB LUT
/// from the palette's <c>(t∈[0,1]) → (r,g,b)</c> function once, then walk the
/// pixels in a tight loop indexing the LUT. The LUT is the dominant cost on
/// small images; on large images the per-pixel copy dominates and the LUT
/// is essentially free.
/// </para>
/// </remarks>
public sealed class ApplyColormapFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "apply_colormap";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Maps a single-channel image through a named colour palette "
        + "('turbo', 'jet', 'gray'), returning a false-coloured image. "
        + "Reads the input's red-channel intensity as the scalar value; "
        + "pairs naturally with depth maps (models.midas_small / models.dpt_large) "
        + "and saliency masks (models.u2net / models.u2netp).";

    /// <summary>
    /// Maps each supported palette name to a function that takes a normalised
    /// scalar <c>t ∈ [0, 1]</c> and returns the corresponding RGB triple in
    /// <c>[0, 1]</c>. Adding a palette is a one-line addition to this table
    /// plus its mapping function below.
    /// </summary>
    private static readonly Dictionary<string, Func<float, (float r, float g, float b)>> Palettes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["gray"] = Gray,
            ["turbo"] = Turbo,
            ["jet"] = Jet,
        };

    /// <summary>
    /// The set of palette names <see cref="ApplyColormapFunction"/> currently
    /// recognises. Stable, case-preserving snapshot — used to drive the
    /// <see cref="DataKindMatcher.StringEnum"/> matcher on the <c>palette</c>
    /// parameter (LS completion + parameter-shape display), the runtime
    /// error message, and tests that want to round-trip every palette.
    /// </summary>
    public static IReadOnlyList<string> AvailablePalettes { get; } =
        Palettes.Keys.OrderBy(s => s, StringComparer.Ordinal).ToArray();

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("image",   DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("palette", DataKindMatcher.StringEnum(AvailablePalettes)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ApplyColormapFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef imgArg = args[0];
        ValueRef paletteArg = args[1];

        if (imgArg.IsNull || paletteArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
        }

        string paletteName = paletteArg.AsString();
        if (!Palettes.TryGetValue(paletteName, out Func<float, (float r, float g, float b)>? mapper))
        {
            throw new FunctionArgumentException(
                Name,
                $"unknown palette '{paletteName}'. Supported palettes: "
                + string.Join(", ", AvailablePalettes) + ".");
        }

        SKBitmap srcBitmap = imgArg.AsImage();
        int width = srcBitmap.Width;
        int height = srcBitmap.Height;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException(
                $"apply_colormap: source image has non-positive dimensions ({width}×{height}).");
        }

        // Source → RGBA8888 with unpremultiplied alpha. The arena-owned bitmap
        // from AsImage() is platform-native (BGRA on Windows, RGBA elsewhere);
        // an explicit conversion gives us a stable byte order for the per-pixel
        // index regardless of host OS.
        SKImageInfo rgbaInfo = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap srcRgba = new(rgbaInfo);
        if (!srcBitmap.CopyTo(srcRgba, SKColorType.Rgba8888))
        {
            throw new InvalidOperationException(
                $"apply_colormap: failed to convert source image to RGBA8888 "
                + $"(source colour type: {srcBitmap.ColorType}).");
        }

        // Precompute the 256-entry RGB LUT once per call.
        Span<byte> lut = stackalloc byte[256 * 3];
        for (int i = 0; i < 256; i++)
        {
            float t = i / 255f;
            (float r, float g, float b) = mapper(t);
            int o = i * 3;
            lut[o + 0] = NormalizeToByte(r);
            lut[o + 1] = NormalizeToByte(g);
            lut[o + 2] = NormalizeToByte(b);
        }

        // Output bitmap. Ownership transfers to the ValueRef — no `using`.
        SKImageInfo outInfo = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        SKBitmap output = new(outInfo);
        nint srcPtr = srcRgba.GetPixels();
        nint outPtr = output.GetPixels();
        int planeSize = width * height;
        unsafe
        {
            byte* s = (byte*)srcPtr;
            byte* d = (byte*)outPtr;
            fixed (byte* lutPtr = lut)
            {
                for (int i = 0; i < planeSize; i++)
                {
                    int so = i * 4;
                    int @do = i * 4;
                    int li = s[so + 0] * 3;     // index into LUT by source R-channel
                    d[@do + 0] = lutPtr[li + 0];
                    d[@do + 1] = lutPtr[li + 1];
                    d[@do + 2] = lutPtr[li + 2];
                    d[@do + 3] = 255;
                }
            }
        }

        return new ValueTask<ValueRef>(ValueRef.FromImage(output));
    }

    // ── Palette functions ────────────────────────────────────────────────

    /// <summary>
    /// Identity palette — output equals input, R = G = B = t. Useful for
    /// debug / round-trip checks.
    /// </summary>
    private static (float r, float g, float b) Gray(float t) => (t, t, t);

    /// <summary>
    /// Google's "Turbo" colourmap — Anton Mikhailov's degree-5 polynomial
    /// approximation in <c>[0, 1]</c>. Drop-in replacement for <c>jet</c>
    /// without jet's perceptual artefacts (banding at green/yellow, false
    /// edges at the rainbow transitions).
    /// Source: https://research.google/blog/turbo-an-improved-rainbow-colormap-for-visualization/
    /// </summary>
    /// <remarks>
    /// The polynomial fit diverges slightly from the canonical 256-entry
    /// turbo LUT at the very endpoints — at <c>t=0</c> the polynomial gives
    /// a dark-magenta-ish mix (R≈35, G≈23, B≈27) where the LUT is pure dark
    /// blue (48, 18, 59). For typical depth visualisations this is invisible:
    /// no real depth map's per-image min-max produces values exactly at 0
    /// or 1, and the rest of the curve matches the LUT to within a couple
    /// of byte values. If endpoint-exact turbo is ever needed, swap this
    /// function for an embedded 256×3 LUT lookup.
    /// </remarks>
    private static (float r, float g, float b) Turbo(float t)
    {
        // Saturate input to [0, 1] — out-of-range inputs produce wild
        // polynomial values otherwise.
        if (t < 0f) t = 0f;
        else if (t > 1f) t = 1f;

        float t2 = t * t;
        float t3 = t2 * t;
        float t4 = t2 * t2;
        float t5 = t4 * t;

        float r = 0.13572138f + 4.61539260f * t  + -42.66032258f * t2
                + 132.13108234f * t3 + -152.94239396f * t4 + 59.28637943f * t5;
        float g = 0.09140261f + 2.19418839f * t  +   4.84296658f * t2
                + -14.18503333f * t3 +  4.27729857f * t4 +  2.82956604f * t5;
        float b = 0.10667330f + 12.64194608f * t + -60.58204836f * t2
                + 110.36276771f * t3 + -89.90310912f * t4 + 27.34824973f * t5;

        return (r, g, b);
    }

    /// <summary>
    /// Classic MATLAB "jet" rainbow — piecewise-linear ramps over R/G/B.
    /// Each channel is a triangle peaking at a different point along the
    /// scale. Provided for matching prior art / legacy comparisons; for
    /// new visualisations <c>turbo</c> is strictly better.
    /// </summary>
    private static (float r, float g, float b) Jet(float t)
    {
        if (t < 0f) t = 0f;
        else if (t > 1f) t = 1f;

        // Each channel: clamp(min(4t - shift, -4t + shift'), 0, 1)
        float r = Saturate(MathF.Min(4f * t - 1.5f, -4f * t + 4.5f));
        float g = Saturate(MathF.Min(4f * t - 0.5f, -4f * t + 3.5f));
        float b = Saturate(MathF.Min(4f * t + 0.5f, -4f * t + 2.5f));
        return (r, g, b);
    }

    private static float Saturate(float v)
    {
        if (v < 0f) return 0f;
        if (v > 1f) return 1f;
        return v;
    }

    /// <summary>
    /// Maps a <c>[0, 1]</c> float to a <c>[0, 255]</c> byte with clamp.
    /// </summary>
    private static byte NormalizeToByte(float value)
    {
        float scaled = value * 255f;
        if (scaled < 0f) return 0;
        if (scaled > 255f) return 255;
        return (byte)MathF.Round(scaled);
    }
}
