using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions.Audio;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Audio;

/// <summary>
/// <c>audio_samples(rate Int32, audio Audio) → Float32[]</c>. Decodes
/// an Audio value to a flat array of PCM samples at the requested
/// sample rate, mono. The audio analog of <c>image_to_tensor_chw</c>:
/// the universal preprocessor that turns a media container into the
/// numeric tensor every downstream model body wants.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Mono only (v1).</strong> Source channel counts other than
/// 1 raise a clear error rather than auto-downmixing. Speech ML
/// models (Whisper, Silero VAD, Wav2Vec2, HuBERT, CLAP audio) all
/// consume mono; surfacing the channel mismatch explicitly avoids
/// silent transformations users would discover only by comparing
/// outputs against a reference. A downmix primitive can land
/// separately when a real consumer needs stereo input.
/// </para>
/// <para>
/// <strong>Rate.</strong> The IDE surfaces the canonical ML/broadcast
/// rates as completion suggestions via the <see cref="InCheck"/> on
/// the parameter, but FFmpeg's resampler handles any positive Int32 —
/// the InCheck list is the well-known set, not a hard whitelist. Add
/// a value to the list when a model genuinely needs an off-list rate
/// and the IDE completion should learn it.
/// </para>
/// <para>
/// <strong>Failure modes.</strong> Returns NULL for NULL Audio.
/// Throws on malformed containers, stereo/multi-channel input, or
/// rates ≤ 0. Returns whatever PCM the source contained — no padding
/// to a requested duration; callers handle short clips.
/// </para>
/// </remarks>
public sealed class AudioSamplesFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "audio_samples";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Decodes an Audio value to a flat Float32 array of PCM samples at "
        + "the requested sample rate, mono. Mirrors image_to_tensor_chw's "
        + "role for image models — the universal preprocessor for audio "
        + "model bodies (Silero VAD, Wav2Vec2, HuBERT, CLAP, future). "
        + "Source channel counts > 1 raise; convert to mono externally for "
        + "now. Common rates: 8000 (telephony), 16000 (speech ML), 22050 "
        + "(librosa default), 24000 / 32000 (Encodec / MusicGen), 44100 "
        + "(CD audio), 48000 (broadcast / CLAP).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("rate", DataKindMatcher.Family(DataKindFamily.IntegerFamily),
                    Metadata: new ParameterMetadata(
                        Check: new InCheck(["8000", "16000", "22050", "24000", "32000", "44100", "48000"]),
                        Unit: "Hz",
                        Description: "Output sample rate. Pass the rate the downstream model expects.")),
                new ParameterSpec("audio", DataKindMatcher.Exact(DataKind.Audio)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<AudioSamplesFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef rateArg = args[0];
        ValueRef audioArg = args[1];

        if (audioArg.IsNull || rateArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.Float32));
        }

        if (!rateArg.TryToInt32(out int rate))
        {
            throw new FunctionArgumentException(Name,
                $"rate of kind {rateArg.Kind} could not be widened to Int32.");
        }
        if (rate <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"rate must be > 0; got {rate}.");
        }

        DataValue audioValue = audioArg.ToDataValue(frame.Source);
        byte[] audioBytes = audioValue.AsAudio(frame.Source, frame.SidecarRegistry);
        try
        {
            float[] samples = AudioPcmDecoder.DecodeMonoFloat32(audioBytes, rate);
            return new ValueTask<ValueRef>(
                ValueRef.FromPrimitiveArray(samples, DataKind.Float32));
        }
        catch (InvalidOperationException ex)
        {
            throw new FunctionArgumentException(Name, ex.Message);
        }
    }
}
