using System.Collections.Immutable;

using DatumIngest.Execution;
using DatumIngest.Functions.Scalar.Drawing;
using DatumIngest.Manifest;
using DatumIngest.Model;

using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Audio;

/// <summary>
/// <c>audio_waveform_path(envelope Array&lt;Float32&gt;(bins, 2), width Int32, height Int32, fill Color) → Drawing</c>.
/// Builds a single closed filled <see cref="PathDrawing"/> tracing the
/// waveform's outline: top edge (max samples) left-to-right, then bottom
/// edge (min samples) right-to-left, then closed. The natural primitive
/// for the smooth filled-curve waveform aesthetic — distinct from the
/// per-column bars look produced by <c>audio_waveform_drawing</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Coordinate convention.</strong> The path is laid out in pixel
/// space: column <c>b</c> sits at <c>x = b * (width - 1) / (bins - 1)</c>
/// and each amplitude <c>a ∈ [-1, 1]</c> maps to
/// <c>y = height/2 - a * height/2</c> (so <c>+1</c> is at the top, <c>-1</c>
/// at the bottom, <c>0</c> at the canvas centreline). The closed path is
/// suitable for compositing onto a background or wrapping in
/// <c>transform(...)</c> / <c>blend(...)</c> for stylised renderings.
/// </para>
/// <para>
/// <strong>Pair with stroked styles.</strong> The function fills the
/// outline; for a stroked silhouette (line-only, no fill), compose with
/// the existing <c>draw_path</c> /<c>stroke_path</c> primitives on the
/// same envelope-derived commands, or stack via <c>group([...])</c> with
/// per-column bars from <c>audio_waveform_drawing</c>.
/// </para>
/// </remarks>
public sealed class AudioWaveformPathFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "audio_waveform_path";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Drawing;

    /// <inheritdoc />
    public static string Description =>
        "Builds a single closed filled Drawing tracing the waveform envelope's "
        + "top + bottom edges. Use for smooth filled-curve waveform visuals; "
        + "compose with group() to stack with per-column bars from "
        + "audio_waveform_drawing for hybrid styles.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("envelope", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("width",    DataKindMatcher.Family(DataKindFamily.IntegerFamily),
                    Metadata: new ParameterMetadata(
                        Check: new GreaterThanCheck(0m),
                        Unit: "pixels",
                        Description: "Canvas width the path is laid out across.")),
                new ParameterSpec("height",   DataKindMatcher.Family(DataKindFamily.IntegerFamily),
                    Metadata: new ParameterMetadata(
                        Check: new GreaterThanCheck(0m),
                        Unit: "pixels",
                        Description: "Canvas height; centreline sits at height/2 and amplitudes scale by height/2.")),
                new ParameterSpec("fill",     DataKindMatcher.Exact(DataKind.Color)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Drawing)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<AudioWaveformPathFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull || args[3].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Drawing));
        }

        int width = args[1].ToInt32();
        int height = args[2].ToInt32();
        if (width <= 0 || height <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"width and height must be > 0; got width={width}, height={height}.");
        }
        SKColor fill = DrawingHelpers.ToSKColor(args[3]);

        DataValue envelopeValue = args[0].ToDataValue(frame.Source);
        int bins;
        if (envelopeValue.IsMultiDim)
        {
            ReadOnlySpan<int> shape = envelopeValue.GetShape(frame.Source, frame.SidecarRegistry);
            if (shape.Length != 2 || shape[1] != 2)
            {
                throw new FunctionArgumentException(Name,
                    $"envelope must be Array<Float32>(bins, 2); got shape "
                    + $"[{string.Join(", ", shape.ToArray())}]. Produce one via "
                    + "audio_waveform_envelope(audio, bins).");
            }
            bins = shape[0];
        }
        else
        {
            throw new FunctionArgumentException(Name,
                "envelope must be a shape-aware Array<Float32>(bins, 2). "
                + "Produce one via audio_waveform_envelope(audio, bins).");
        }
        if (bins <= 0)
        {
            return new ValueTask<ValueRef>(ValueRef.FromDrawing(new PathDrawing(
                ImmutableArray<PathCommand>.Empty, Fill: fill)));
        }

        ReadOnlySpan<float> envelope = envelopeValue.AsArraySpan<float>(frame.Source, frame.SidecarRegistry);
        if (envelope.Length != bins * 2)
        {
            throw new FunctionArgumentException(Name,
                $"envelope declared shape ({bins}, 2) = {bins * 2} elements doesn't "
                + $"match the array's actual element count {envelope.Length}.");
        }

        ImmutableArray<PathCommand> commands = BuildOutlineCommands(envelope, bins, width, height);
        return new ValueTask<ValueRef>(
            ValueRef.FromDrawing(new PathDrawing(commands, Fill: fill)));
    }

    /// <summary>
    /// Constructs the closed-path command sequence tracing the envelope's
    /// top edge (max) left-to-right and the bottom edge (min)
    /// right-to-left, then closing. Coordinates are in pixel space.
    /// </summary>
    private static ImmutableArray<PathCommand> BuildOutlineCommands(
        ReadOnlySpan<float> envelope, int bins, int width, int height)
    {
        ImmutableArray<PathCommand>.Builder cmds =
            ImmutableArray.CreateBuilder<PathCommand>(bins * 2 + 2);
        float midY = height * 0.5f;
        // Endpoint-inclusive layout: bin 0 at x=0, bin (bins-1) at x=width-1.
        // Single-bin envelopes collapse to x=0 — fine for a degenerate path.
        double xStep = bins > 1 ? (width - 1.0) / (bins - 1) : 0.0;

        // Top edge: move to (x_0, y_max_0), line through the rest.
        float yTop0 = midY - envelope[0 * 2 + 1] * midY;
        cmds.Add(new PathMove(new SKPoint(0f, yTop0)));
        for (int b = 1; b < bins; b++)
        {
            float x = (float)(b * xStep);
            float yTop = midY - envelope[b * 2 + 1] * midY;
            cmds.Add(new PathLine(new SKPoint(x, yTop)));
        }
        // Bottom edge: line back right-to-left through the min envelope.
        for (int b = bins - 1; b >= 0; b--)
        {
            float x = (float)(b * xStep);
            float yBot = midY - envelope[b * 2 + 0] * midY;
            cmds.Add(new PathLine(new SKPoint(x, yBot)));
        }
        cmds.Add(new PathClose());
        return cmds.ToImmutable();
    }
}
