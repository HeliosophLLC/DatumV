using System.IO.Hashing;
using Heliosoph.DatumV.Functions.Audio;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Serialization.MediaBag;

/// <summary>
/// <see cref="MediaKindHandler"/> for audio bags: FLAC, WAV, OGG, MP3 entries.
/// Emits (file_name, file, file_sample_rate, file_channels, file_bit_depth,
/// file_duration_ms, file_byte_length). Inline metadata is populated for
/// containers <see cref="AudioHeaderParser"/> recognises (WAV today); other
/// formats produce nulls for the sample-rate/channels/bit-depth/duration
/// columns and accessors fall through to a full decode.
/// </summary>
/// <remarks>
/// Adding FLAC STREAMINFO parsing (~50 LOC in <see cref="AudioHeaderParser"/>)
/// would populate the inline metadata for LibriSpeech-style archives without
/// any change here — the handler routes everything through <see cref="AudioHeaderParser.TryParseHeader"/>.
/// </remarks>
internal sealed class AudioKindHandler : MediaKindHandler
{
    public static readonly AudioKindHandler Instance = new();
    private AudioKindHandler() { }

    public override DataKind Kind => DataKind.Audio;

    public override string[] ColumnNames { get; } =
    [
        "file_name",
        "file",
        "file_sample_rate",
        "file_channels",
        "file_bit_depth",
        "file_duration_ms",
        "file_byte_length",
    ];

    public override bool MatchesMagic(ReadOnlySpan<byte> magic)
    {
        // FLAC: "fLaC"
        if (magic.Length >= 4 && magic[0] == (byte)'f' && magic[1] == (byte)'L'
            && magic[2] == (byte)'a' && magic[3] == (byte)'C')
            return true;

        // WAV: RIFF....WAVE
        if (magic.Length >= 12
            && magic[0] == (byte)'R' && magic[1] == (byte)'I' && magic[2] == (byte)'F' && magic[3] == (byte)'F'
            && magic[8] == (byte)'W' && magic[9] == (byte)'A' && magic[10] == (byte)'V' && magic[11] == (byte)'E')
            return true;

        // OGG: "OggS"
        if (magic.Length >= 4 && magic[0] == (byte)'O' && magic[1] == (byte)'g'
            && magic[2] == (byte)'g' && magic[3] == (byte)'S')
            return true;

        // MP3: ID3v2 tag or MPEG frame sync (FF Ex/Fx with valid layer bits).
        if (magic.Length >= 3 && magic[0] == (byte)'I' && magic[1] == (byte)'D' && magic[2] == (byte)'3')
            return true;
        if (magic.Length >= 2 && magic[0] == 0xFF && (magic[1] & 0xE0) == 0xE0)
            return true;

        return false;
    }

    public override void PopulateRowFromArena(
        DataValue[] values, string fullName,
        long arenaOffset, int actualLength,
        ReadOnlySpan<byte> bytes,
        Arena arena)
    {
        AudioMetadata? meta = AudioHeaderParser.TryParseHeader(bytes);
        uint hash32 = unchecked((uint)XxHash64.HashToUInt64(bytes));

        values[0] = DataValue.FromString(fullName, arena);
        values[1] = AudioDataValueFactory.FromArenaOffset(arenaOffset, actualLength, meta, hash32);
        PopulateDerived(values, meta, actualLength);
    }

    public override void PopulateRowFromSidecar(
        DataValue[] values, string fullName,
        long sidecarOffset, long sidecarLength,
        ReadOnlySpan<byte> bytes,
        Arena arena)
    {
        AudioMetadata? meta = AudioHeaderParser.TryParseHeader(bytes);
        uint hash32 = unchecked((uint)XxHash64.HashToUInt64(bytes));

        values[0] = DataValue.FromString(fullName, arena);
        values[1] = AudioDataValueFactory.FromSidecar(sidecarOffset, sidecarLength, meta, hash32);
        PopulateDerived(values, meta, sidecarLength);
    }

    private static void PopulateDerived(DataValue[] values, AudioMetadata? meta, long byteLength)
    {
        values[6] = DataValue.FromInt64(byteLength);

        if (meta is null || meta.SampleRate == 0)
        {
            values[2] = DataValue.Null(DataKind.Int32);
            values[3] = DataValue.Null(DataKind.UInt8);
            values[4] = DataValue.Null(DataKind.UInt8);
            values[5] = DataValue.Null(DataKind.Int64);
            return;
        }

        values[2] = DataValue.FromInt32(checked((int)meta.SampleRate));
        values[3] = DataValue.FromUInt8(meta.Channels);
        values[4] = DataValue.FromUInt8(meta.BitDepth);

        // Duration in milliseconds: frame_count / sample_rate * 1000. Computed in
        // long-math to dodge intermediate overflow on multi-hour streams.
        long durationMs = meta.FrameCount > 0
            ? (long)meta.FrameCount * 1000L / meta.SampleRate
            : 0L;
        values[5] = meta.FrameCount > 0 ? DataValue.FromInt64(durationMs) : DataValue.Null(DataKind.Int64);
    }
}
