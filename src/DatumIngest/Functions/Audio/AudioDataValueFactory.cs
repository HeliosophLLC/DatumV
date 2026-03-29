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
}
