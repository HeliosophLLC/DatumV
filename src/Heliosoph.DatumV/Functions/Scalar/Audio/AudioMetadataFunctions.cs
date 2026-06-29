using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions.Audio;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Audio;

/// <summary>
/// <c>audio_sample_rate(a Audio) → Int32</c>. Returns the sample rate (Hz) from
/// the audio value's inline metadata. Every Audio production site routes through
/// <see cref="Heliosoph.DatumV.Functions.Audio.AudioDataValueFactory"/>, which parses the
/// container header (WAV today) and stamps the metadata. Returns NULL only when
/// the format wasn't recognised by <see cref="Heliosoph.DatumV.Functions.Audio.AudioHeaderParser"/>
/// (e.g. MP3/FLAC/OGG until those formats are added to the parser).
/// </summary>
/// <remarks>
/// First Audio metadata accessor — companion functions (<c>audio_channels</c>,
/// <c>audio_bit_depth</c>, <c>audio_frame_count</c>) follow the same pattern; add
/// them when needed.
/// </remarks>
public sealed class AudioSampleRateFunction : IFunction, IScalarFunction, IInlineMetadataAccessor
{
    /// <inheritdoc />
    public static string Name => "audio_sample_rate";

    /// <inheritdoc />
    public InlineAccessorField Field => InlineAccessorField.AudioSampleRate;

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Returns the sample rate in Hz of an Audio value as Int32, or NULL when the "
        + "metadata was not stamped at ingest.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("a", DataKindMatcher.Exact(DataKind.Audio))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<AudioSampleRateFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Int32));
        }
        uint rate = arg.InlineDataValue.AudioSampleRate;
        return new ValueTask<ValueRef>(
            rate != 0
                ? ValueRef.FromInt32(checked((int)rate))
                : ValueRef.Null(DataKind.Int32));
    }
}

/// <summary>
/// <c>audio_duration(a Audio) → Float64</c>. Returns the clip length in seconds.
/// On the common (stamped) path the value is derived inline as
/// <c>audio_frame_count ÷ audio_sample_rate</c> — exact for WAV / FLAC, and
/// served by the planner-time elider without dispatching here at all. When the
/// frame count wasn't stamped at ingest (lossy MP3 / OGG headers don't surface
/// it cheaply), this falls back to a decode-free container-duration read via
/// <see cref="AudioPcmDecoder.TryReadDurationSeconds"/>. Returns NULL only when
/// neither source records a duration (rare streamed captures) or the container
/// has no decodable audio stream.
/// </summary>
public sealed class AudioDurationFunction : IFunction, IScalarFunction, IInlineMetadataAccessor
{
    /// <inheritdoc />
    public static string Name => "audio_duration";

    /// <inheritdoc />
    public InlineAccessorField Field => InlineAccessorField.AudioDuration;

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Returns the duration in seconds of an Audio value as Float64. Computed "
        + "inline from the stamped frame count and sample rate when available "
        + "(WAV/FLAC), else from a decode-free container-duration read (MP3/OGG). "
        + "NULL when the container records no duration.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("a", DataKindMatcher.Exact(DataKind.Audio))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float64)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<AudioDurationFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float64));
        }

        // Fast path mirrors the elider: exact when both fields are stamped.
        DataValue dv = arg.InlineDataValue;
        uint frames = dv.AudioFrameCount;
        uint rate = dv.AudioSampleRate;
        if (frames != 0 && rate != 0)
        {
            return new ValueTask<ValueRef>(ValueRef.FromFloat64(frames / (double)rate));
        }

        // Slow path: read the container's recorded duration without decoding PCM.
        DataValue audioValue = arg.ToDataValue(frame.Source);
        byte[] audioBytes = audioValue.AsAudio(frame.Source, frame.SidecarRegistry);
        try
        {
            double? seconds = AudioPcmDecoder.TryReadDurationSeconds(audioBytes);
            return new ValueTask<ValueRef>(
                seconds is { } s ? ValueRef.FromFloat64(s) : ValueRef.Null(DataKind.Float64));
        }
        catch (InvalidOperationException ex)
        {
            throw new FunctionArgumentException(Name, ex.Message);
        }
    }
}
