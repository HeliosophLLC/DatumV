using DatumIngest.Execution;
using DatumIngest.Functions.Audio;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Audio;

/// <summary>
/// <c>audio_decode(bytes Array&lt;UInt8&gt;) → Audio</c>. Wraps raw encoded
/// audio bytes (WAV / FLAC / OGG / MP3 / M4A, anything the audio header
/// parser or downstream decoder recognises) as a typed <c>Audio</c> value
/// so subsequent audio functions (<c>audio_sample_rate</c>,
/// <c>audio_samples</c>, <c>audio_to_mono</c>, …) can consume it directly.
/// The proximate use case is SQL recipes that compose
/// <c>audio_decode(open_archive(:source, …).bytes)</c> to lift a
/// <c>.flac</c> or <c>.wav</c> entry pulled out of an archive into the
/// engine's typed-audio surface without ever touching disk.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What "decode" means here.</strong> No PCM decoding happens — the
/// bytes pass through verbatim with the kind tag flipped to
/// <see cref="DataKind.Audio"/>. The header parser
/// (<see cref="AudioHeaderParser"/>) is invoked at the
/// <see cref="AudioDataValueFactory.FromEncodedBytes"/> boundary so the
/// resulting <c>Audio</c> value carries inline metadata (sample rate,
/// channels, bit depth, frame count) for WAV and FLAC sources; other
/// formats fall through to the no-metadata path and <c>audio_*</c>
/// accessors return NULL until the parser learns them.
/// </para>
/// <para>
/// <strong>Compositional shape.</strong> The canonical use is in CTAS over
/// a media-bag archive: <c>SELECT audio_decode(o.bytes) AS clip
/// FROM open_archive(:source, '%.flac') o</c>. For LJSpeech the JOIN form
/// composes naturally with <c>read_csv</c> on the manifest. No allocation
/// beyond what the typed-Audio storage path already does — bytes are
/// either arena-resident (default) or routed to a <c>.datum-blob</c>
/// sidecar at write time.
/// </para>
/// </remarks>
public sealed class AudioDecodeFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "audio_decode";

    /// <inheritdoc />
    // Matches the placeholder category every other Audio scalar uses; an
    // `Audio` enum value would be cleaner but is a cross-cutting rename.
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Wraps a raw encoded-audio byte array as a typed Audio value: "
        + "audio_decode(bytes Array<UInt8>) → Audio. Parses the container "
        + "header (WAV / FLAC today) to stamp inline metadata so accessors "
        + "like audio_sample_rate() skip a full decode. Bytes pass through "
        + "verbatim — no PCM materialization. Composes with open_archive: "
        + "audio_decode(o.bytes) lifts a .flac / .wav archive entry into "
        + "the engine's typed-audio surface for downstream audio functions.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("bytes", DataKindMatcher.Exact(DataKind.UInt8),
                    IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Audio)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<AudioDecodeFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef bytesArg = arguments.Span[0];
        if (bytesArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Audio));
        }

        // Pass the bytes through with the kind flipped to Audio. The header parse +
        // inline-metadata stamping happens at the materialization boundary — when
        // this ValueRef gets converted to a DataValue (via FromEncodedBytes in the
        // standard dispatch table), the AudioMetadata is filled in. Keeping it lazy
        // here avoids parsing every byte array even for queries that never read
        // the resulting Audio value (LIMIT cutoffs, filtered branches).
        byte[] bytes = bytesArg.AsBytes();
        return new ValueTask<ValueRef>(ValueRef.FromBytes(DataKind.Audio, bytes));
    }
}
