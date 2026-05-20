using System.Collections.Immutable;

using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Execution.Contexts;
using Heliosoph.DatumV.Functions.Scalar.Drawing;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Audio;

/// <summary>
/// <c>audio_waveform_drawing(envelope Array&lt;Float32&gt;(bins, 2), render Lambda) → Drawing</c>.
/// Walks a precomputed waveform envelope and invokes the user's lambda once
/// per bin to build a per-column Drawing, returning the assembled tree as a
/// <see cref="GroupDrawing"/>. The lambda is scoped to
/// <see cref="WaveformContext"/> so it receives <c>(t, min, max)</c> — the
/// column's normalised position in <c>[0, 1]</c> and the bin's amplitude
/// extremes in <c>[-1, 1]</c> — and is expected to return a Drawing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why no width / height parameter.</strong> Pixel-space math
/// happens entirely in the lambda body; the canvas dimensions are
/// expressions the caller already names at the call site and the lambda
/// captures by closure. Decoupling the iteration from a target size makes
/// the same envelope reusable across multiple renderings at different
/// resolutions without re-binning.
/// </para>
/// <para>
/// <strong>Normalisation of <c>t</c>.</strong> For <c>bins ≥ 2</c> the
/// lambda sees <c>t = b / (bins - 1)</c>, so the first bin gets
/// <c>t = 0</c> and the last gets <c>t = 1</c> (endpoint-inclusive).
/// Single-bin envelopes emit <c>t = 0</c> for the sole call.
/// </para>
/// <para>
/// <strong>Null propagation.</strong> Null envelope, null lambda, or a
/// lambda invocation that returns null produces a null Drawing for that
/// bin; the empty group of null bins still groups (returning an empty
/// Drawing for an all-null result) rather than collapsing to null —
/// composition with siblings in an outer <c>group([...])</c> stays
/// predictable.
/// </para>
/// </remarks>
public sealed class AudioWaveformDrawingFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "audio_waveform_drawing";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Drawing;

    /// <inheritdoc />
    public static string Description =>
        "Invokes a (t, min, max) -> Drawing lambda once per bin of a waveform "
        + "envelope and groups the results into a single Drawing. Pair with "
        + "audio_waveform_envelope to produce the envelope, then render the "
        + "Drawing through render(...) to rasterise it onto an Image at the "
        + "desired size.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("envelope", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("render",   DataKindMatcher.Lambda(
                                                  WaveformContext.Name,
                                                  DataKindMatcher.Exact(DataKind.Drawing))),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Drawing)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<AudioWaveformDrawingFunction>(argumentKinds);

    /// <inheritdoc />
    public async ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        // Snapshot the args out of the Span so we can cross await
        // boundaries when invoking the lambda.
        ValueRef envelopeArg;
        ValueRef lambda;
        {
            ReadOnlySpan<ValueRef> args = arguments.Span;
            if (args[0].IsNull || args[1].IsNull)
            {
                return ValueRef.Null(DataKind.Drawing);
            }
            envelopeArg = args[0];
            lambda = args[1];
        }

        if (frame.LambdaInvoker is null)
        {
            throw new InvalidOperationException(
                $"{Name} requires an ILambdaInvoker on the evaluation frame. "
                + "The query pipeline auto-attaches one via ExpressionEvaluator; "
                + "this error indicates a frame built outside that pipeline.");
        }

        // Resolve the envelope's (bins, 2) shape. Accept rank-2 directly;
        // anything else points the user at audio_waveform_envelope as the
        // canonical producer of the right shape.
        DataValue envelopeValue = envelopeArg.ToDataValue(frame.Source);
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
            // A zero-bin envelope groups to an empty Drawing rather than
            // erroring — composing with siblings stays predictable.
            return ValueRef.FromDrawing(new GroupDrawing(ImmutableArray<DrawingPayload>.Empty));
        }

        ReadOnlySpan<float> envelope = envelopeValue.AsArraySpan<float>(frame.Source, frame.SidecarRegistry);
        if (envelope.Length != bins * 2)
        {
            throw new FunctionArgumentException(Name,
                $"envelope declared shape ({bins}, 2) = {bins * 2} elements doesn't "
                + $"match the array's actual element count {envelope.Length}.");
        }

        // Copy envelope contents out of the span before the await loop —
        // spans can't cross awaits and the lambda body is async.
        float[] envelopeCopy = envelope.ToArray();

        ImmutableArray<DrawingPayload>.Builder children =
            ImmutableArray.CreateBuilder<DrawingPayload>(bins);
        ValueRef[] lambdaArgs = new ValueRef[3];
        float tDenominator = bins > 1 ? bins - 1 : 1;
        for (int b = 0; b < bins; b++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            float t = bins > 1 ? b / tDenominator : 0f;
            float lo = envelopeCopy[b * 2 + 0];
            float hi = envelopeCopy[b * 2 + 1];
            lambdaArgs[0] = ValueRef.FromFloat32(t);
            lambdaArgs[1] = ValueRef.FromFloat32(lo);
            lambdaArgs[2] = ValueRef.FromFloat32(hi);

            ValueRef result = await frame.LambdaInvoker.InvokeLambdaAsync(
                lambda, lambdaArgs, frame, cancellationToken).ConfigureAwait(false);
            if (result.IsNull)
            {
                continue;
            }
            children.Add(result.AsDrawing());
        }

        return ValueRef.FromDrawing(new GroupDrawing(children.ToImmutable()));
    }
}
