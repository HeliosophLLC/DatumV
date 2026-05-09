using System.Buffers.Binary;

namespace DatumIngest.Functions.Audio;

/// <summary>
/// Lightweight audio container header parser. Inspects the first few bytes of an
/// encoded audio blob and returns sample rate / channels / bit depth / frame count
/// when the format is recognised. Used at ingest time to stamp inline metadata onto
/// <see cref="DatumIngest.Model.DataValue"/>s of kind <c>Audio</c> so that
/// <c>audio_sample_rate()</c> and friends can skip a full decode.
/// </summary>
/// <remarks>
/// <para>Supports WAV (RIFF/WAVE), FLAC (STREAMINFO), MP3 (MPEG audio frame header
/// with optional ID3v2 prefix), and OGG (Vorbis identification + Opus head).
/// Other formats return <c>null</c> — extend with per-format readers as needed.</para>
/// <para>The parser is intentionally permissive: returns <c>null</c> for any
/// malformed input rather than throwing. Callers fall back to the no-metadata
/// factory and accessors return zero sentinels.</para>
/// <para>Lossy formats (MP3, OGG Vorbis, OGG Opus) report <c>BitDepth=0</c>
/// because there isn't a meaningful integer bit depth — sample resolution is
/// codec-internal. <c>FrameCount</c> is also surfaced as <c>0</c> for these
/// formats because deriving it from the prefix requires either Xing/VBRI tag
/// parsing (MP3) or seeking to the last page (OGG); both are out of scope for
/// the lightweight header parse contract. Callers that need duration for
/// lossy sources should compute it from a full decode.</para>
/// </remarks>
public static class AudioHeaderParser
{
    /// <summary>
    /// Attempts to parse audio metadata from the prefix of an encoded audio blob.
    /// Returns <c>null</c> when the format isn't recognised or the header is malformed.
    /// </summary>
    public static AudioMetadata? TryParseHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) return null;

        // RIFF/WAVE: "RIFF" .... "WAVE"
        if (data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F'
            && data[8] == (byte)'W' && data[9] == (byte)'A' && data[10] == (byte)'V' && data[11] == (byte)'E')
        {
            return TryParseWave(data);
        }

        // FLAC: "fLaC" magic followed by metadata blocks; STREAMINFO is mandatory
        // and must be the first block.
        if (data[0] == (byte)'f' && data[1] == (byte)'L' && data[2] == (byte)'a' && data[3] == (byte)'C')
        {
            return TryParseFlac(data);
        }

        // MP3: either an ID3v2 tag prefix ("ID3") followed by MPEG frames, or
        // a raw MPEG audio frame starting with the 11-bit frame sync (0xFFE).
        if (data[0] == (byte)'I' && data[1] == (byte)'D' && data[2] == (byte)'3')
        {
            return TryParseMp3WithId3(data);
        }
        if (data[0] == 0xFF && (data[1] & 0xE0) == 0xE0)
        {
            return TryParseMp3Frame(data);
        }

        // OGG: "OggS" page-header capture pattern. The first page of an OGG
        // bitstream carries either a Vorbis identification packet or an Opus
        // OpusHead packet; both expose channel count, and Vorbis additionally
        // carries the source sample rate.
        if (data[0] == (byte)'O' && data[1] == (byte)'g' && data[2] == (byte)'g' && data[3] == (byte)'S')
        {
            return TryParseOgg(data);
        }

        return null;
    }

    private static AudioMetadata? TryParseWave(ReadOnlySpan<byte> data)
    {
        // Walk the chunk list starting after the 12-byte RIFF/WAVE header. Each chunk
        // is `id (4) + size (4 LE) + payload`. The fmt chunk gives us sample_rate /
        // channels / bit_depth; the data chunk's size + bit_depth give the frame
        // count. We don't care about extra metadata chunks; first fmt + first data is
        // sufficient.
        int cursor = 12;
        ushort channels = 0;
        uint sampleRate = 0;
        ushort bitsPerSample = 0;
        uint dataBytes = 0;
        bool seenFmt = false;
        bool seenData = false;

        while (cursor + 8 <= data.Length && (!seenFmt || !seenData))
        {
            ReadOnlySpan<byte> id = data.Slice(cursor, 4);
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(cursor + 4, 4));
            int payloadStart = cursor + 8;
            int payloadEnd = payloadStart + (int)chunkSize;
            if (payloadEnd > data.Length) break;

            if (!seenFmt && id[0] == (byte)'f' && id[1] == (byte)'m' && id[2] == (byte)'t' && id[3] == (byte)' '
                && chunkSize >= 16)
            {
                // ushort format_code, ushort channels, uint sample_rate, uint byte_rate,
                // ushort block_align, ushort bits_per_sample
                channels = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(payloadStart + 2, 2));
                sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(payloadStart + 4, 4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(payloadStart + 14, 2));
                seenFmt = true;
            }
            else if (!seenData && id[0] == (byte)'d' && id[1] == (byte)'a' && id[2] == (byte)'t' && id[3] == (byte)'a')
            {
                dataBytes = chunkSize;
                seenData = true;
            }

            // Chunks are word-aligned: pad odd chunk sizes by 1.
            cursor = payloadEnd + ((int)chunkSize & 1);
        }

        if (!seenFmt || sampleRate == 0) return null;

        // Frame count = data bytes / (channels × bytes_per_sample). Returns 0 when the
        // data chunk wasn't in the parse window (extremely large files where data is
        // after the peek limit) — sample rate / channels / bit_depth are still useful.
        uint frameCount = 0;
        if (seenData && channels > 0 && bitsPerSample > 0)
        {
            uint frameBytes = (uint)channels * (uint)(bitsPerSample / 8);
            if (frameBytes > 0) frameCount = dataBytes / frameBytes;
        }

        return new AudioMetadata(sampleRate, (byte)Math.Min(channels, (ushort)255), (byte)Math.Min(bitsPerSample, (ushort)255), frameCount);
    }

    private static AudioMetadata? TryParseFlac(ReadOnlySpan<byte> data)
    {
        // FLAC stream layout (https://xiph.org/flac/format.html):
        //   bytes 0..3   : "fLaC" magic
        //   bytes 4..7   : METADATA_BLOCK_HEADER — top bit is last-block flag, low 7
        //                  bits are block type (STREAMINFO=0 must come first per spec),
        //                  next 24 bits are payload length (=34 for STREAMINFO).
        //   bytes 8..41  : STREAMINFO payload (34 bytes).
        //
        // The fields we care about sit at bytes 18..25 of the file (offset 10 of the
        // STREAMINFO payload), packed across a 64-bit big-endian word as
        //   sample_rate (20b) | channels-1 (3b) | bits_per_sample-1 (5b) | total_samples (36b).
        // We don't need to read the trailing MD5 / min/max block / min/max frame size
        // fields — 26 bytes is enough to extract everything inline-metadata cares about.
        if (data.Length < 26) return null;

        int blockType = data[4] & 0x7F;
        if (blockType != 0) return null; // STREAMINFO must be the first metadata block.

        ulong packed = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(18, 8));

        uint sampleRate = (uint)((packed >> 44) & 0xF_FFFFu);
        if (sampleRate == 0) return null; // spec disallows zero sample rate.

        // 3-bit channels field stores channels-1, so encoded 0..7 → actual 1..8.
        byte channels = (byte)(((packed >> 41) & 0x7u) + 1u);

        // 5-bit bits-per-sample field stores bits-per-sample-1, so raw 4..32. The
        // inline metadata slot only models 8/16/24/32; anything else (rare in FLAC
        // — typically only 4-bit medical archives or 20-bit archival masters)
        // gets surfaced as 0 (unknown) so PackAudioP4 doesn't throw downstream.
        byte rawBits = (byte)(((packed >> 36) & 0x1Fu) + 1u);
        byte bitsPerSample = rawBits switch
        {
            8 or 16 or 24 or 32 => rawBits,
            _ => 0,
        };

        // FLAC's "total_samples" is the inter-channel sample count, which equals the
        // frame count (one frame == one sample-per-channel). Clamp the 36-bit field
        // to 32-bit; cap bites at ~27 h @ 44.1 kHz / ~12 h @ 96 kHz, well beyond any
        // realistic ML-dataset utterance. Value 0 is "unknown" per FLAC spec and is
        // legitimately allowed (streams of unknown length); we preserve it.
        ulong totalSamplesRaw = packed & 0xF_FFFF_FFFFUL;
        uint totalSamples = totalSamplesRaw > uint.MaxValue ? uint.MaxValue : (uint)totalSamplesRaw;

        return new AudioMetadata(sampleRate, channels, bitsPerSample, totalSamples);
    }

    private static AudioMetadata? TryParseMp3WithId3(ReadOnlySpan<byte> data)
    {
        // ID3v2 header layout (10 bytes):
        //   bytes 0..2: "ID3"
        //   bytes 3..4: version (major, revision)
        //   byte 5:     flags
        //   bytes 6..9: tag size as a 28-bit synchsafe integer (each byte uses only
        //               its low 7 bits — the high bit is always 0 so the encoded
        //               size can never collide with the MPEG frame sync). The size
        //               does NOT include the 10-byte header itself.
        // After the tag (10 + size bytes) comes the first MPEG audio frame.
        if (data.Length < 10) return null;

        int tagSize = ((data[6] & 0x7F) << 21)
                    | ((data[7] & 0x7F) << 14)
                    | ((data[8] & 0x7F) << 7)
                    | (data[9] & 0x7F);

        int frameStart = 10 + tagSize;
        if (frameStart < 0 || frameStart + 4 > data.Length) return null;

        // Must hit a real frame sync at frameStart, otherwise the tag size was
        // bogus or the file is truncated.
        if (data[frameStart] != 0xFF || (data[frameStart + 1] & 0xE0) != 0xE0) return null;

        return ParseMp3FrameHeader(data.Slice(frameStart));
    }

    private static AudioMetadata? TryParseMp3Frame(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return null;
        return ParseMp3FrameHeader(data);
    }

    private static AudioMetadata? ParseMp3FrameHeader(ReadOnlySpan<byte> data)
    {
        // MPEG audio frame header is 32 bits across 4 bytes:
        //   byte 1: sync(3) | version(2) | layer(2) | protection(1)
        //   byte 2: bitrate(4) | sample_rate(2) | padding(1) | private(1)
        //   byte 3: channel_mode(2) | mode_ext(2) | copyright(1) | original(1) | emphasis(2)
        // Version encoding: 00=MPEG 2.5, 01=reserved, 10=MPEG 2, 11=MPEG 1.
        // Layer encoding: 00=reserved, 01=Layer III, 10=Layer II, 11=Layer I.
        // Reject reserved combinations — they signal a malformed or non-MPEG payload.
        int version = (data[1] >> 3) & 0x3;
        if (version == 1) return null;
        int layer = (data[1] >> 1) & 0x3;
        if (layer == 0) return null;

        int sampleRateIdx = (data[2] >> 2) & 0x3;
        if (sampleRateIdx == 3) return null;

        // Sample rate lookup by (version, index). MPEG 1 covers CD-quality and
        // broadcast rates; MPEG 2 and MPEG 2.5 halve and quarter them
        // respectively for narrowband / streaming use cases.
        uint sampleRate = (version, sampleRateIdx) switch
        {
            (3, 0) => 44100u, (3, 1) => 48000u, (3, 2) => 32000u, // MPEG 1
            (2, 0) => 22050u, (2, 1) => 24000u, (2, 2) => 16000u, // MPEG 2
            (0, 0) => 11025u, (0, 1) => 12000u, (0, 2) => 8000u,  // MPEG 2.5
            _ => 0u,
        };
        if (sampleRate == 0) return null;

        // Channel mode: 00=stereo, 01=joint stereo, 10=dual channel, 11=mono.
        // Anything other than 11 (mono) is 2-channel for accessor purposes.
        int channelMode = (data[3] >> 6) & 0x3;
        byte channels = channelMode == 3 ? (byte)1 : (byte)2;

        // BitDepth: MP3 is lossy with codec-internal precision — not a meaningful
        // integer bit-depth field, so surface 0 (unknown).
        // FrameCount: deriving it requires either Xing/VBRI VBR tag parsing or
        // a full file scan plus bitrate-table lookup. Out of scope for the
        // lightweight prefix parse — return 0 and let callers compute via decode.
        return new AudioMetadata(sampleRate, channels, BitDepth: 0, FrameCount: 0);
    }

    private static AudioMetadata? TryParseOgg(ReadOnlySpan<byte> data)
    {
        // OGG page header layout (27 bytes + segment_table):
        //   bytes 0..3:   "OggS" capture pattern
        //   byte 4:       stream_structure_version (=0)
        //   byte 5:       header_type flags (0x02 = beginning of stream on first page)
        //   bytes 6..13:  granule_position (8 bytes, codec-specific timestamp)
        //   bytes 14..17: bitstream_serial_number
        //   bytes 18..21: page_sequence_number
        //   bytes 22..25: CRC32
        //   byte 26:      number_page_segments (N)
        //   bytes 27..(27+N-1): segment_table — N segment-length lacing values
        // The first packet payload starts immediately after the segment table.
        if (data.Length < 27) return null;

        int segmentCount = data[26];
        int headerSize = 27 + segmentCount;
        if (data.Length < headerSize + 1) return null;

        ReadOnlySpan<byte> packet = data[headerSize..];

        // Vorbis identification packet: starts with byte 0x01 (packet type =
        // identification), then 6-byte "vorbis" magic, then sample rate at
        // bytes 12..15. Total ID packet is 30 bytes.
        if (packet.Length >= 16
            && packet[0] == 0x01
            && packet[1] == (byte)'v' && packet[2] == (byte)'o' && packet[3] == (byte)'r'
            && packet[4] == (byte)'b' && packet[5] == (byte)'i' && packet[6] == (byte)'s')
        {
            return ParseVorbisIdentification(packet);
        }

        // Opus identification: starts with "OpusHead" magic, then 1-byte version,
        // 1-byte channel count, etc. (19 bytes for the basic head packet.)
        if (packet.Length >= 10
            && packet[0] == (byte)'O' && packet[1] == (byte)'p' && packet[2] == (byte)'u' && packet[3] == (byte)'s'
            && packet[4] == (byte)'H' && packet[5] == (byte)'e' && packet[6] == (byte)'a' && packet[7] == (byte)'d')
        {
            return ParseOpusHead(packet);
        }

        return null;
    }

    private static AudioMetadata? ParseVorbisIdentification(ReadOnlySpan<byte> packet)
    {
        // Vorbis identification packet (after the 7-byte "\x01vorbis" preamble):
        //   bytes 7..10:  vorbis_version (uint32_le, must be 0)
        //   byte 11:      audio_channels
        //   bytes 12..15: audio_sample_rate (uint32_le)
        //   bytes 16..27: bitrate_max / nom / min (12 bytes total, unused here)
        //   byte 28:      blocksize_0/_1 packed
        //   byte 29:      framing_flag (must be 1)
        if (packet.Length < 16) return null;
        byte channels = packet[11];
        if (channels == 0) return null;
        uint sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(12, 4));
        if (sampleRate == 0) return null;

        return new AudioMetadata(sampleRate, channels, BitDepth: 0, FrameCount: 0);
    }

    private static AudioMetadata? ParseOpusHead(ReadOnlySpan<byte> packet)
    {
        // OpusHead packet (after the 8-byte "OpusHead" magic):
        //   byte 8:       version (=1 for current encoders)
        //   byte 9:       channel_count
        //   bytes 10..11: pre_skip (uint16_le, decoder warm-up samples)
        //   bytes 12..15: input_sample_rate (uint32_le, source rate hint — Opus
        //                 internally always decodes at 48 kHz regardless)
        //   bytes 16..17: output_gain (int16_le)
        //   byte 18:      channel_mapping family
        if (packet.Length < 10) return null;
        byte channels = packet[9];
        if (channels == 0) return null;

        // Report 48000 — Opus's actual decoding rate, regardless of the input
        // hint at bytes 12..15. This matches what audio_samples() produces, so
        // audio_sample_rate(clip) == audio_samples(48000, clip).Length / channels
        // holds the round-trip invariant users expect.
        return new AudioMetadata(SampleRate: 48000, channels, BitDepth: 0, FrameCount: 0);
    }
}

/// <summary>
/// Audio metadata extracted from a container header. Channels and bit depth are
/// clamped to <see cref="byte"/> (255 max — covers every real-world value).
/// </summary>
public sealed record AudioMetadata(uint SampleRate, byte Channels, byte BitDepth, uint FrameCount);
