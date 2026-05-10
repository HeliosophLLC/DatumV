using DatumIngest.Execution;
using DatumIngest.Functions.Audio;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Audio;

/// <summary>
/// <c>audio_waveform_envelope(audio Audio, bins Int32) → Array&lt;Float32&gt;(bins, 2)</c>.
/// Decodes the audio to mono PCM at the source's native sample rate, then
/// folds the samples into <c>bins</c> evenly-sized bins and
/// emits the per-bin <c>(min, max)</c> amplitude envelope as a shape-aware
/// 2-D Float32 array. The numeric primitive underneath the waveform
/// visualisation stack — useful on its own for analysis, ML feature
/// extraction, or custom rendering paths that don't go through
/// <c>audio_waveform_drawing</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Layout.</strong> Row-major <c>(bins, 2)</c>: column 0 holds the
/// bin minimum, column 1 the bin maximum. Both are in the raw float-PCM
/// amplitude range (nominally <c>[-1, 1]</c>, but the resampler does not
/// clamp so clipped sources may exceed slightly). Read individual values
/// via <c>array_get(env, b, 0)</c> / <c>array_get(env, b, 1)</c>; introspect
/// dimensions via <c>array_shape</c>.
/// </para>
/// <para>
/// <strong>Binning convention.</strong> Bin <c>b</c> covers samples
/// <c>floor(b * N / bins)</c> through <c>floor((b+1) * N / bins)</c>
/// (half-open), where <c>N</c> is the decoded sample count. When
/// <c>bins &gt; N</c> some bins receive no samples and emit
/// <c>(0, 0)</c>; the same applies to empty audio. This is the peak
/// envelope — RMS or log envelopes can land as sibling functions when a
/// real consumer needs them.
/// </para>
/// <para>
/// <strong>Channels.</strong> Multi-channel sources are auto-downmixed
/// to mono via the FFmpeg default channel mixer (the same downmix
/// <c>audio_to_mono</c> uses). Visualising per-channel envelopes
/// independently can land as a separate function later.
/// </para>
/// </remarks>
public sealed class AudioWaveformEnvelopeFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "audio_waveform_envelope";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Folds an Audio value into a (bins, 2) Float32 peak envelope: column 0 "
        + "is per-bin min amplitude, column 1 is per-bin max amplitude. Multi-"
        + "channel sources are auto-downmixed to mono. The numeric substrate "
        + "for audio_waveform_drawing — useful directly when you want analysis "
        + "or custom rendering rather than the bundled draw path.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("audio", DataKindMatcher.Exact(DataKind.Audio)),
                new ParameterSpec("bins",  DataKindMatcher.Family(DataKindFamily.IntegerFamily),
                    Metadata: new ParameterMetadata(
                        Check: new GreaterThanCheck(0m),
                        Unit: "bins",
                        Description: "Number of envelope bins; typically the target image width in pixels.")),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<AudioWaveformEnvelopeFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef audioArg = args[0];
        ValueRef binsArg = args[1];

        if (audioArg.IsNull || binsArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.Float32));
        }

        if (!binsArg.TryToInt32(out int bins))
        {
            throw new FunctionArgumentException(Name,
                $"bins of kind {binsArg.Kind} could not be widened to Int32.");
        }
        if (bins <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"bins must be > 0; got {bins}.");
        }

        DataValue audioValue = audioArg.ToDataValue(frame.Source);
        byte[] audioBytes = audioValue.AsAudio(frame.Source, frame.SidecarRegistry);

        float[] samples;
        try
        {
            samples = AudioPcmDecoder.DecodeDownmixedFloat32(audioBytes, out _);
        }
        catch (InvalidOperationException ex)
        {
            throw new FunctionArgumentException(Name, ex.Message);
        }

        float[] envelope = ComputeEnvelope(samples, bins);
        return new ValueTask<ValueRef>(
            ValueRef.FromPrimitiveMultiDimArray(envelope, [bins, 2], DataKind.Float32));
    }

    /// <summary>
    /// Folds <paramref name="samples"/> into <paramref name="bins"/> bins of
    /// (min, max) peaks. Output is flat row-major: index <c>b*2 + 0</c> holds
    /// bin <c>b</c>'s minimum, index <c>b*2 + 1</c> the maximum. Empty bins
    /// (when <paramref name="bins"/> exceeds the sample count, or when the
    /// audio decoded to nothing) emit <c>(0, 0)</c>.
    /// </summary>
    private static float[] ComputeEnvelope(ReadOnlySpan<float> samples, int bins)
    {
        float[] envelope = new float[bins * 2];
        int n = samples.Length;
        if (n == 0) return envelope;

        // Use double for the per-bin span boundary to avoid drift across
        // many bins (a float ratio accumulates rounding when bins is large).
        double samplesPerBin = (double)n / bins;
        for (int b = 0; b < bins; b++)
        {
            int start = (int)System.Math.Floor(b * samplesPerBin);
            int end = (int)System.Math.Floor((b + 1) * samplesPerBin);
            if (end > n) end = n;
            if (start >= end)
            {
                // Empty bin: more bins than samples, or trailing partial.
                continue;
            }
            float lo = samples[start];
            float hi = lo;
            for (int i = start + 1; i < end; i++)
            {
                float v = samples[i];
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
            envelope[b * 2 + 0] = lo;
            envelope[b * 2 + 1] = hi;
        }
        return envelope;
    }
}
