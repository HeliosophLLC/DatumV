using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Audio;

/// <summary>
/// <c>audio_sample_rate(a Audio) → Int32</c>. Returns the sample rate (Hz) from
/// the audio value's inline metadata. Every Audio production site routes through
/// <see cref="DatumIngest.Functions.Audio.AudioDataValueFactory"/>, which parses the
/// container header (WAV today) and stamps the metadata. Returns NULL only when
/// the format wasn't recognised by <see cref="DatumIngest.Functions.Audio.AudioHeaderParser"/>
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
