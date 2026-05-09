using DatumIngest.Model;

namespace DatumIngest.Functions.Audio;

/// <summary>
/// Helpers for constructing <see cref="DataValue"/>s of <see cref="DataKind.Audio"/>
/// with inline sample-rate / channels / bit-depth / frame-count metadata populated.
/// Mirrors <c>ImageDataValueFactory</c> / future <c>VideoDataValueFactory</c>; every
/// new Audio production site should route through here so accessors like
/// <c>audio_sample_rate()</c> work without a decode fallback.
/// </summary>
/// <remarks>
/// <see cref="AudioHeaderParser"/> recognises WAV today; other formats (MP3, FLAC,
/// OGG) currently fall through to the no-metadata factory. Adding a format is a
/// matter of extending the parser — wiring stays the same.
/// </remarks>
public static class AudioDataValueFactory
{
    /// <summary>
    /// Constructs a <see cref="DataKind.Audio"/> <see cref="DataValue"/> from encoded
    /// audio bytes, parsing the container header (when recognised) to stamp inline
    /// metadata. Unrecognised formats produce a metadata-free value; accessors return
    /// zero sentinels and the SQL <c>audio_*</c> functions return NULL.
    /// </summary>
    public static DataValue FromEncodedBytes(byte[] bytes, IValueStore store)
    {
        AudioMetadata? meta = AudioHeaderParser.TryParseHeader(bytes);
        if (meta is { SampleRate: > 0 })
        {
            return DataValue.FromAudio(bytes, store, meta.SampleRate, meta.Channels, meta.BitDepth, meta.FrameCount);
        }
        return DataValue.FromAudio(bytes, store);
    }

    /// <summary>
    /// Constructs a <see cref="DataKind.Audio"/> <see cref="DataValue"/> referencing
    /// encoded audio bytes already appended to an arena at <paramref name="offset"/> /
    /// <paramref name="length"/>. Stamps inline metadata from <paramref name="meta"/>
    /// (when WAV header parsed) plus <paramref name="hash32"/> for cross-arena
    /// Equals short-circuit. Mirrors <c>ImageDataValueFactory.FromArenaOffset</c>.
    /// </summary>
    public static DataValue FromArenaOffset(long offset, int length, AudioMetadata? meta, uint hash32)
    {
        if (meta is { SampleRate: > 0 })
        {
            return DataValue.FromAudioAtOffset(offset, length,
                meta.SampleRate, meta.Channels, meta.BitDepth, meta.FrameCount, hash32);
        }
        return DataValue.FromAudioAtOffset(offset, length);
    }

    /// <summary>
    /// Constructs a <see cref="DataKind.Audio"/> <see cref="DataValue"/> referencing
    /// encoded audio bytes already written to a <c>.datum-blob</c> sidecar. The
    /// underlying <see cref="DataValue.FromAudioInSidecar(long, long, byte)"/> does not
    /// carry inline metadata today, so <paramref name="meta"/> and
    /// <paramref name="hash32"/> are currently dropped; <c>audio_*</c> accessors fall
    /// through to a full decode for sidecar-backed audio. The parameters are kept on
    /// the signature so that adding a metadata-bearing <c>FromAudioInSidecar</c>
    /// overload later is a single-line wire-up.
    /// </summary>
    public static DataValue FromSidecar(long offset, long length, AudioMetadata? meta, uint hash32, byte storeId = 0)
    {
        _ = meta;
        _ = hash32;
        return DataValue.FromAudioInSidecar(offset, length, storeId);
    }
}
