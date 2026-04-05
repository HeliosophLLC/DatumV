using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Image;

/// <summary>
/// <c>frames_to_gif(frames Array&lt;Image&gt;, fps Float32) → Image</c>.
/// Encodes an array of equally-sized frame Images as a single animated
/// GIF89a Image, looping forever at the supplied frame rate.
/// </summary>
/// <remarks>
/// <para>
/// Pairs with <c>animate_frames(duration, fps, size, lambda)</c> — the
/// natural shape is
/// <c>SELECT frames_to_gif(animate_frames(1.0, 12, point2d(64, 64), …), 12)</c>.
/// The two functions intentionally share the <c>fps</c> argument: the
/// caller chooses the per-frame delay independently from how many frames
/// were rendered, which lets you produce a "slow motion" or "fast preview"
/// GIF from the same frame array without re-rendering.
/// </para>
/// <para>
/// The encoder uses a single global 256-colour palette (median-cut over
/// the union of opaque pixels across all frames) and dispose-to-background
/// frames. See <see cref="GifEncoder"/> for the v1 trade-offs (no
/// dithering, no inter-frame diffs).
/// </para>
/// <para>
/// Null array, null fps, or an empty array all return a null Image. Any
/// null element inside the array is treated as an entirely-transparent
/// frame of the canvas size — chosen over throwing so a CASE expression
/// that occasionally yields a null Drawing doesn't poison the encode.
/// </para>
/// </remarks>
public sealed class FramesToGifFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "frames_to_gif";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Drawing;

    /// <inheritdoc />
    public static string Description =>
        "Encodes an Array<Image> of equally-sized frames as a looping animated "
        + "GIF89a Image at the supplied frame rate. Pairs with animate_frames.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("frames", DataKindMatcher.Exact(DataKind.Image), IsArray: ArrayMatch.Array),
                new ParameterSpec("fps",    DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public int QueryUnitCost => 500;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<FramesToGifFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
        }

        float fps = args[1].ToFloat();
        if (fps <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"fps must be positive; got {fps}.");
        }

        ReadOnlySpan<ValueRef> elements = args[0].GetArrayElements();
        if (elements.Length == 0)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
        }

        // Materialise to a managed array so we can iterate without span-lifetime
        // worries (no awaits here today, but the encoder loop allocates and
        // disposes bitmaps and we want a stable view).
        ValueRef[] frames = elements.ToArray();

        // Determine canvas dimensions from the first non-null frame. Null
        // entries are filled in later as transparent placeholders of the
        // same size — the encoder rejects mismatched sizes outright.
        int canvasWidth = 0;
        int canvasHeight = 0;
        SKBitmap? firstBitmap = null;
        int firstIdx = -1;
        for (int i = 0; i < frames.Length; i++)
        {
            if (!frames[i].IsNull)
            {
                firstBitmap = frames[i].AsImage();
                canvasWidth = firstBitmap.Width;
                canvasHeight = firstBitmap.Height;
                firstIdx = i;
                break;
            }
        }
        if (firstBitmap is null)
        {
            // Every frame was null — nothing to encode.
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
        }
        if (canvasWidth <= 0 || canvasHeight <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"frame[{firstIdx}] has non-positive dimensions ({canvasWidth}×{canvasHeight}).");
        }

        // Build the SKBitmap list. Track which bitmaps we allocated as
        // placeholders so we can dispose them after encoding — the bitmaps
        // we pulled from existing ValueRefs are owned by those refs.
        List<SKBitmap> bitmaps = new(frames.Length);
        List<SKBitmap> ownedPlaceholders = new();
        try
        {
            for (int i = 0; i < frames.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (frames[i].IsNull)
                {
                    SKBitmap blank = new(new SKImageInfo(
                        canvasWidth, canvasHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul));
                    using (SKCanvas canvas = new(blank))
                    {
                        canvas.Clear(SKColors.Transparent);
                    }
                    bitmaps.Add(blank);
                    ownedPlaceholders.Add(blank);
                    continue;
                }
                SKBitmap bmp = frames[i].AsImage();
                if (bmp.Width != canvasWidth || bmp.Height != canvasHeight)
                {
                    throw new FunctionArgumentException(Name,
                        $"frames must all share dimensions; frame[{firstIdx}] is "
                        + $"{canvasWidth}×{canvasHeight} but frame[{i}] is {bmp.Width}×{bmp.Height}.");
                }
                bitmaps.Add(bmp);
            }

            // Convert fps → centiseconds per frame. GIF stores delay in cs (1/100s).
            // Round to the nearest centisecond and clamp to at least 1 — a 0-delay
            // frame asks the decoder to show "as fast as possible", which most
            // players interpret as ~10cs anyway and confuses the user-facing fps.
            int delayCs = (int)System.Math.Max(1, System.Math.Round(100.0 / fps));

            byte[] gifBytes = GifEncoder.Encode(bitmaps, delayCs);
            return new ValueTask<ValueRef>(ValueRef.FromImage(gifBytes));
        }
        finally
        {
            foreach (SKBitmap bmp in ownedPlaceholders)
            {
                bmp.Dispose();
            }
        }
    }
}
