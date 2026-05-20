using System.Buffers.Binary;
using Heliosoph.DatumV.Functions.Audio;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Audio;

/// <summary>
/// Round-trip tests for the WAV header parser + the audio inline-metadata stamping path.
/// </summary>
public sealed class AudioHeaderParserTests : ServiceTestBase
{
    [Fact]
    public void TryParseHeader_MinimalWave_ExtractsSampleRateChannelsBitDepthFrameCount()
    {
        // Build a synthetic WAV: RIFF + fmt + data with one second of stereo 16-bit @ 44.1 kHz.
        const uint sampleRate = 44100;
        const ushort channels = 2;
        const ushort bitsPerSample = 16;
        const uint frameCount = 44100; // 1 second
        uint dataBytes = frameCount * channels * (bitsPerSample / 8u);

        byte[] wav = BuildWave(sampleRate, channels, bitsPerSample, dataBytes);

        AudioMetadata? meta = AudioHeaderParser.TryParseHeader(wav);
        Assert.NotNull(meta);
        Assert.Equal(sampleRate, meta!.SampleRate);
        Assert.Equal((byte)channels, meta.Channels);
        Assert.Equal((byte)bitsPerSample, meta.BitDepth);
        Assert.Equal(frameCount, meta.FrameCount);
    }

    [Fact]
    public void TryParseHeader_NotWave_ReturnsNull()
    {
        byte[] notWav = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0]; // JPEG signature shape
        Assert.Null(AudioHeaderParser.TryParseHeader(notWav));
    }

    [Fact]
    public void TryParseHeader_TooShort_ReturnsNull()
    {
        byte[] truncated = [0x52, 0x49, 0x46]; // "RIF"
        Assert.Null(AudioHeaderParser.TryParseHeader(truncated));
    }

    [Fact]
    public void AudioDataValueFactory_FromEncodedBytes_StampsMetadataOntoDataValue()
    {
        byte[] wav = BuildWave(sampleRate: 48000, channels: 1, bitsPerSample: 24, dataBytes: 144000);
        using Arena store = CreateArena();

        DataValue dv = AudioDataValueFactory.FromEncodedBytes(wav, store);

        Assert.Equal(DataKind.Audio, dv.Kind);
        Assert.Equal(48000u, dv.AudioSampleRate);
        Assert.Equal((byte)1, dv.AudioChannels);
        Assert.Equal((byte)24, dv.AudioBitDepth);
        Assert.Equal(48000u, dv.AudioFrameCount); // 144000 / (1 channel * 3 bytes) = 48000 frames
    }

    [Fact]
    public void AudioDataValueFactory_UnknownFormat_FallsBackToNoMetadata()
    {
        // Not a recognised audio format — factory should produce a valid Audio value
        // with zero-sentinel metadata so accessors return 0 and SQL functions return NULL.
        byte[] mystery = new byte[256];
        mystery[0] = 0xFF; // arbitrary
        using Arena store = CreateArena();

        DataValue dv = AudioDataValueFactory.FromEncodedBytes(mystery, store);

        Assert.Equal(DataKind.Audio, dv.Kind);
        Assert.Equal(0u, dv.AudioSampleRate);
        Assert.Equal((byte)0, dv.AudioChannels);
        Assert.Equal((byte)0, dv.AudioBitDepth);
        Assert.Equal(0u, dv.AudioFrameCount);
    }

    // ───────────────────────── FLAC ─────────────────────────

    [Fact]
    public void TryParseHeader_MinimalFlac_ExtractsSampleRateChannelsBitDepthFrameCount()
    {
        // LibriSpeech-shaped FLAC: 16 kHz mono 16-bit, ~2 s of audio (32000 frames).
        byte[] flac = BuildFlac(sampleRate: 16000, channels: 1, bitsPerSample: 16, totalSamples: 32000);

        AudioMetadata? meta = AudioHeaderParser.TryParseHeader(flac);

        Assert.NotNull(meta);
        Assert.Equal(16000u, meta!.SampleRate);
        Assert.Equal((byte)1, meta.Channels);
        Assert.Equal((byte)16, meta.BitDepth);
        Assert.Equal(32000u, meta.FrameCount);
    }

    [Fact]
    public void TryParseHeader_StandardCdQualityFlac_ExtractsExpectedFields()
    {
        // 44.1 kHz stereo 16-bit — bog-standard CD-quality FLAC.
        byte[] flac = BuildFlac(sampleRate: 44100, channels: 2, bitsPerSample: 16, totalSamples: 441000); // 10 s

        AudioMetadata? meta = AudioHeaderParser.TryParseHeader(flac);

        Assert.NotNull(meta);
        Assert.Equal(44100u, meta!.SampleRate);
        Assert.Equal((byte)2, meta.Channels);
        Assert.Equal((byte)16, meta.BitDepth);
        Assert.Equal(441000u, meta.FrameCount);
    }

    [Fact]
    public void TryParseHeader_HiResFlac_24BitMultichannel_ExtractsAllFields()
    {
        // 96 kHz 6-channel 24-bit — audiophile multichannel.
        byte[] flac = BuildFlac(sampleRate: 96000, channels: 6, bitsPerSample: 24, totalSamples: 96000);

        AudioMetadata? meta = AudioHeaderParser.TryParseHeader(flac);

        Assert.NotNull(meta);
        Assert.Equal(96000u, meta!.SampleRate);
        Assert.Equal((byte)6, meta.Channels);
        Assert.Equal((byte)24, meta.BitDepth);
        Assert.Equal(96000u, meta.FrameCount);
    }

    [Fact]
    public void TryParseHeader_FlacWithExoticBitDepth_ClampsBitDepthToZero()
    {
        // 20-bit FLAC — legal per the FLAC spec but not in the inline-metadata's
        // {8, 16, 24, 32} set; parser should surface bit_depth=0 (unknown) rather
        // than propagate a value that would throw at the PackAudioP4 boundary.
        byte[] flac = BuildFlac(sampleRate: 48000, channels: 2, bitsPerSample: 20, totalSamples: 48000);

        AudioMetadata? meta = AudioHeaderParser.TryParseHeader(flac);

        Assert.NotNull(meta);
        Assert.Equal(48000u, meta!.SampleRate);
        Assert.Equal((byte)2, meta.Channels);
        Assert.Equal((byte)0, meta.BitDepth); // clamped from 20
        Assert.Equal(48000u, meta.FrameCount);
    }

    [Fact]
    public void TryParseHeader_FlacWithUnknownTotalSamples_ReportsZeroFrameCount()
    {
        // FLAC spec allows total_samples=0 to mean "unknown" (live streams etc.).
        // Parser preserves zero rather than coercing — accessors return 0, SQL
        // duration functions return NULL, downstream operators handle it.
        byte[] flac = BuildFlac(sampleRate: 44100, channels: 2, bitsPerSample: 16, totalSamples: 0);

        AudioMetadata? meta = AudioHeaderParser.TryParseHeader(flac);

        Assert.NotNull(meta);
        Assert.Equal(44100u, meta!.SampleRate);
        Assert.Equal(0u, meta.FrameCount);
    }

    [Fact]
    public void TryParseHeader_FlacTruncatedBeforeStreamInfo_ReturnsNull()
    {
        byte[] truncated = [(byte)'f', (byte)'L', (byte)'a', (byte)'C', 0x80, 0x00, 0x00, 0x22];
        Assert.Null(AudioHeaderParser.TryParseHeader(truncated));
    }

    [Fact]
    public void TryParseHeader_FlacWithZeroSampleRate_ReturnsNull()
    {
        byte[] flac = BuildFlac(sampleRate: 0, channels: 2, bitsPerSample: 16, totalSamples: 0);
        Assert.Null(AudioHeaderParser.TryParseHeader(flac));
    }

    [Fact]
    public void TryParseHeader_FlacWithNonStreamInfoFirstBlock_ReturnsNull()
    {
        // Spec violation: first metadata block must be STREAMINFO (type 0). A FLAC-
        // magic prefix with a non-zero block-type byte is malformed and surfaced as
        // unparseable rather than producing junk metadata.
        byte[] flac = BuildFlac(sampleRate: 44100, channels: 2, bitsPerSample: 16, totalSamples: 44100);
        flac[4] = 0x83; // last-block=1, type=3 (VORBIS_COMMENT) — not STREAMINFO

        Assert.Null(AudioHeaderParser.TryParseHeader(flac));
    }

    [Fact]
    public void AudioDataValueFactory_FromEncodedBytes_StampsFlacMetadataOntoDataValue()
    {
        // End-to-end through AudioDataValueFactory — exercises the FLAC parse + the
        // inline metadata stamping path that powers audio_sample_rate() and friends
        // for LibriSpeech-style FLAC archives at ingest time.
        byte[] flac = BuildFlac(sampleRate: 16000, channels: 1, bitsPerSample: 16, totalSamples: 32000);
        using Arena store = CreateArena();

        DataValue dv = AudioDataValueFactory.FromEncodedBytes(flac, store);

        Assert.Equal(DataKind.Audio, dv.Kind);
        Assert.Equal(16000u, dv.AudioSampleRate);
        Assert.Equal((byte)1, dv.AudioChannels);
        Assert.Equal((byte)16, dv.AudioBitDepth);
        Assert.Equal(32000u, dv.AudioFrameCount);
    }

    // ───────────────────────── MP3 ─────────────────────────

    [Fact]
    public void TryParseHeader_Mp3RawFrame_44k1Stereo_ExtractsSampleRateAndChannels()
    {
        // MPEG 1 Layer III, 44.1 kHz, joint stereo — bog-standard MP3 frame.
        byte[] mp3 = BuildMp3Frame(version: Mp3Version.Mpeg1, sampleRateIdx: 0, channelMode: Mp3ChannelMode.JointStereo);

        AudioMetadata? meta = AudioHeaderParser.TryParseHeader(mp3);

        Assert.NotNull(meta);
        Assert.Equal(44100u, meta!.SampleRate);
        Assert.Equal((byte)2, meta.Channels);
        Assert.Equal((byte)0, meta.BitDepth);   // lossy — no integer bit depth
        Assert.Equal(0u, meta.FrameCount);      // frame count from prefix not derivable
    }

    [Fact]
    public void TryParseHeader_Mp3RawFrame_Mpeg2_22k05Mono_CommonVoiceShape()
    {
        // Common Voice ships MP3 clips at 22.05 kHz mono — exactly this shape.
        byte[] mp3 = BuildMp3Frame(version: Mp3Version.Mpeg2, sampleRateIdx: 0, channelMode: Mp3ChannelMode.Mono);

        AudioMetadata? meta = AudioHeaderParser.TryParseHeader(mp3);

        Assert.NotNull(meta);
        Assert.Equal(22050u, meta!.SampleRate);
        Assert.Equal((byte)1, meta.Channels);
    }

    [Fact]
    public void TryParseHeader_Mp3WithId3v2Prefix_SkipsTagAndFindsFirstFrame()
    {
        // Real MP3 files almost always start with an ID3v2 tag (cover art, title,
        // artist). The parser has to walk past the synchsafe-encoded tag size to
        // find the first MPEG frame.
        byte[] frame = BuildMp3Frame(version: Mp3Version.Mpeg1, sampleRateIdx: 1, channelMode: Mp3ChannelMode.Stereo);
        byte[] id3Payload = new byte[123]; // arbitrary "tag content"
        byte[] file = BuildId3v2Wrapped(id3Payload, frame);

        AudioMetadata? meta = AudioHeaderParser.TryParseHeader(file);

        Assert.NotNull(meta);
        Assert.Equal(48000u, meta!.SampleRate);
        Assert.Equal((byte)2, meta.Channels);
    }

    [Fact]
    public void TryParseHeader_Mp3WithCorruptId3TagSize_ReturnsNull()
    {
        // ID3 tag claims a much larger size than the buffer holds — the parser
        // walks off the end looking for the frame sync and should surface null
        // rather than reading garbage or throwing.
        byte[] file = new byte[64];
        file[0] = (byte)'I'; file[1] = (byte)'D'; file[2] = (byte)'3';
        file[3] = 4; file[4] = 0; // version 2.4.0
        file[5] = 0;              // no flags
        // Synchsafe size that points well past the buffer (~2 MB).
        file[6] = 0x7F; file[7] = 0x7F; file[8] = 0x7F; file[9] = 0x7F;

        Assert.Null(AudioHeaderParser.TryParseHeader(file));
    }

    [Fact]
    public void TryParseHeader_Mp3WithReservedVersionBits_ReturnsNull()
    {
        byte[] mp3 = BuildMp3Frame(version: Mp3Version.Mpeg1, sampleRateIdx: 0, channelMode: Mp3ChannelMode.Stereo);
        // Force the version bits to 01 (reserved).
        mp3[1] = (byte)((mp3[1] & 0b1110_0111) | (1 << 3));

        Assert.Null(AudioHeaderParser.TryParseHeader(mp3));
    }

    [Fact]
    public void AudioDataValueFactory_FromEncodedBytes_StampsMp3MetadataOntoDataValue()
    {
        // End-to-end through the inline-metadata stamping factory — same shape as
        // the WAV / FLAC equivalents, exercises the audio_sample_rate() fast path
        // for MP3 ingest (Common Voice, AudioSet TSVs, podcast archives).
        byte[] mp3 = BuildMp3Frame(version: Mp3Version.Mpeg1, sampleRateIdx: 0, channelMode: Mp3ChannelMode.Stereo);
        using Arena store = CreateArena();

        DataValue dv = AudioDataValueFactory.FromEncodedBytes(mp3, store);

        Assert.Equal(DataKind.Audio, dv.Kind);
        Assert.Equal(44100u, dv.AudioSampleRate);
        Assert.Equal((byte)2, dv.AudioChannels);
        Assert.Equal((byte)0, dv.AudioBitDepth);
    }

    // ───────────────────────── OGG ─────────────────────────

    [Fact]
    public void TryParseHeader_OggVorbis_48kStereo_ExtractsSampleRateAndChannels()
    {
        byte[] ogg = BuildOggVorbis(sampleRate: 48000, channels: 2);

        AudioMetadata? meta = AudioHeaderParser.TryParseHeader(ogg);

        Assert.NotNull(meta);
        Assert.Equal(48000u, meta!.SampleRate);
        Assert.Equal((byte)2, meta.Channels);
        Assert.Equal((byte)0, meta.BitDepth);
        Assert.Equal(0u, meta.FrameCount);
    }

    [Fact]
    public void TryParseHeader_OggVorbis_16kMono_SpeechShape()
    {
        byte[] ogg = BuildOggVorbis(sampleRate: 16000, channels: 1);

        AudioMetadata? meta = AudioHeaderParser.TryParseHeader(ogg);

        Assert.NotNull(meta);
        Assert.Equal(16000u, meta!.SampleRate);
        Assert.Equal((byte)1, meta.Channels);
    }

    [Fact]
    public void TryParseHeader_OggOpus_StereoChannels_ReportsActual48kDecodingRate()
    {
        // Opus internally always decodes at 48 kHz regardless of the input-rate
        // hint in the OpusHead packet. The parser reports 48000 so the value
        // matches what audio_samples() actually produces.
        byte[] ogg = BuildOggOpus(channels: 2, inputSampleRateHint: 16000);

        AudioMetadata? meta = AudioHeaderParser.TryParseHeader(ogg);

        Assert.NotNull(meta);
        Assert.Equal(48000u, meta!.SampleRate);
        Assert.Equal((byte)2, meta.Channels);
    }

    [Fact]
    public void TryParseHeader_OggWithUnknownCodec_ReturnsNull()
    {
        // OGG page wrapping a codec we don't know (neither Vorbis nor Opus).
        byte[] ogg = BuildOggWithPayload([0x01, (byte)'s', (byte)'p', (byte)'e', (byte)'e', (byte)'x', 0, 0, 0]);

        Assert.Null(AudioHeaderParser.TryParseHeader(ogg));
    }

    [Fact]
    public void TryParseHeader_OggTruncatedHeader_ReturnsNull()
    {
        byte[] truncated = [(byte)'O', (byte)'g', (byte)'g', (byte)'S'];
        Assert.Null(AudioHeaderParser.TryParseHeader(truncated));
    }

    [Fact]
    public void AudioDataValueFactory_FromEncodedBytes_StampsOggVorbisMetadataOntoDataValue()
    {
        byte[] ogg = BuildOggVorbis(sampleRate: 44100, channels: 2);
        using Arena store = CreateArena();

        DataValue dv = AudioDataValueFactory.FromEncodedBytes(ogg, store);

        Assert.Equal(DataKind.Audio, dv.Kind);
        Assert.Equal(44100u, dv.AudioSampleRate);
        Assert.Equal((byte)2, dv.AudioChannels);
        Assert.Equal((byte)0, dv.AudioBitDepth);
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static byte[] BuildWave(uint sampleRate, ushort channels, ushort bitsPerSample, uint dataBytes)
    {
        // RIFF header (12) + fmt chunk (8+16) + data chunk (8 + dataBytes)
        int fmtPayload = 16;
        int total = 12 + 8 + fmtPayload + 8 + (int)dataBytes;
        byte[] buf = new byte[total];
        int cursor = 0;

        WriteAscii(buf, ref cursor, "RIFF");
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(cursor, 4), (uint)(total - 8)); cursor += 4;
        WriteAscii(buf, ref cursor, "WAVE");

        WriteAscii(buf, ref cursor, "fmt ");
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(cursor, 4), (uint)fmtPayload); cursor += 4;
        // format_code (PCM=1), channels, sample_rate, byte_rate, block_align, bits_per_sample
        ushort blockAlign = (ushort)(channels * (bitsPerSample / 8));
        uint byteRate = sampleRate * blockAlign;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(cursor, 2), 1); cursor += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(cursor, 2), channels); cursor += 2;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(cursor, 4), sampleRate); cursor += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(cursor, 4), byteRate); cursor += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(cursor, 2), blockAlign); cursor += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(cursor, 2), bitsPerSample); cursor += 2;

        WriteAscii(buf, ref cursor, "data");
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(cursor, 4), dataBytes); cursor += 4;
        // data payload stays zero-filled.

        return buf;
    }

    private static void WriteAscii(byte[] buf, ref int cursor, string ascii)
    {
        for (int i = 0; i < ascii.Length; i++) buf[cursor++] = (byte)ascii[i];
    }

    private static byte[] BuildFlac(uint sampleRate, byte channels, byte bitsPerSample, ulong totalSamples)
    {
        // Minimal FLAC: 4 magic + 4 metadata-block header + 34 STREAMINFO payload.
        byte[] buf = new byte[42];

        // "fLaC"
        buf[0] = (byte)'f'; buf[1] = (byte)'L'; buf[2] = (byte)'a'; buf[3] = (byte)'C';

        // METADATA_BLOCK_HEADER: last-block=1, type=0 (STREAMINFO), length=34 (0x22).
        buf[4] = 0x80;
        buf[5] = 0x00; buf[6] = 0x00; buf[7] = 0x22;

        // STREAMINFO bytes 8..17: min/max block size + min/max frame size — placeholder values.
        // (The parser doesn't read these; we leave them as zeros except for plausible block sizes.)
        buf[8] = 0x10; buf[9] = 0x00;   // min_block_size = 4096
        buf[10] = 0x10; buf[11] = 0x00; // max_block_size = 4096
        // bytes 12..17 stay zero.

        // bytes 18..25: packed sample_rate (20b) | channels-1 (3b) | bits_per_sample-1 (5b) | total_samples (36b).
        ulong packed = ((ulong)(sampleRate & 0xF_FFFFu) << 44)
                     | ((ulong)((channels - 1) & 0x7u) << 41)
                     | ((ulong)((bitsPerSample - 1) & 0x1Fu) << 36)
                     | (totalSamples & 0xF_FFFF_FFFFUL);
        BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(18, 8), packed);

        // bytes 26..41: MD5 of unencoded audio — left zero (parser doesn't read it).
        return buf;
    }

    private enum Mp3Version { Mpeg25 = 0, Mpeg2 = 2, Mpeg1 = 3 }
    private enum Mp3ChannelMode { Stereo = 0, JointStereo = 1, DualChannel = 2, Mono = 3 }

    /// <summary>
    /// Builds a minimal MP3 with a single 32-bit MPEG audio frame header followed
    /// by enough trailing padding to look like a real frame body. The parser only
    /// touches the 4-byte header — body content doesn't matter for these tests.
    /// </summary>
    private static byte[] BuildMp3Frame(Mp3Version version, int sampleRateIdx, Mp3ChannelMode channelMode)
    {
        // Layer III, bitrate index 9, no CRC.
        const int layer = 1;
        const int bitrateIdx = 9;
        const int protection = 1;

        byte b0 = 0xFF;
        byte b1 = (byte)(
            0b1110_0000
            | (((int)version) & 0b11) << 3
            | (layer & 0b11) << 1
            | (protection & 0b1));
        byte b2 = (byte)(
            (bitrateIdx & 0b1111) << 4
            | (sampleRateIdx & 0b11) << 2);
        byte b3 = (byte)(((int)channelMode & 0b11) << 6);

        byte[] buf = new byte[64];
        buf[0] = b0; buf[1] = b1; buf[2] = b2; buf[3] = b3;
        return buf;
    }

    /// <summary>
    /// Wraps <paramref name="frame"/> in an ID3v2.4 tag containing
    /// <paramref name="tagPayload"/> as opaque content. The synchsafe size field
    /// is computed from the payload length so the parser walks the correct offset.
    /// </summary>
    private static byte[] BuildId3v2Wrapped(byte[] tagPayload, byte[] frame)
    {
        byte[] buf = new byte[10 + tagPayload.Length + frame.Length];
        buf[0] = (byte)'I'; buf[1] = (byte)'D'; buf[2] = (byte)'3';
        buf[3] = 4; buf[4] = 0;
        buf[5] = 0;

        int size = tagPayload.Length;
        buf[6] = (byte)((size >> 21) & 0x7F);
        buf[7] = (byte)((size >> 14) & 0x7F);
        buf[8] = (byte)((size >> 7) & 0x7F);
        buf[9] = (byte)(size & 0x7F);

        Array.Copy(tagPayload, 0, buf, 10, tagPayload.Length);
        Array.Copy(frame, 0, buf, 10 + tagPayload.Length, frame.Length);
        return buf;
    }

    /// <summary>
    /// Builds a minimal OGG bitstream prefix carrying a Vorbis identification
    /// packet (30 bytes) on the first page.
    /// </summary>
    private static byte[] BuildOggVorbis(uint sampleRate, byte channels)
    {
        byte[] packet = new byte[30];
        packet[0] = 0x01;
        packet[1] = (byte)'v'; packet[2] = (byte)'o'; packet[3] = (byte)'r';
        packet[4] = (byte)'b'; packet[5] = (byte)'i'; packet[6] = (byte)'s';
        packet[11] = channels;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), sampleRate);
        packet[29] = 0x01;
        return BuildOggWithPayload(packet);
    }

    /// <summary>
    /// Builds a minimal OGG bitstream prefix carrying an Opus identification
    /// (OpusHead) packet on the first page.
    /// </summary>
    private static byte[] BuildOggOpus(byte channels, uint inputSampleRateHint)
    {
        byte[] packet = new byte[19];
        packet[0] = (byte)'O'; packet[1] = (byte)'p'; packet[2] = (byte)'u'; packet[3] = (byte)'s';
        packet[4] = (byte)'H'; packet[5] = (byte)'e'; packet[6] = (byte)'a'; packet[7] = (byte)'d';
        packet[8] = 1;
        packet[9] = channels;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), inputSampleRateHint);
        packet[18] = 0;
        return BuildOggWithPayload(packet);
    }

    /// <summary>
    /// Builds an OGG page header (27 bytes) + single-segment segment table
    /// pointing at <paramref name="packet"/>, then the packet payload.
    /// </summary>
    private static byte[] BuildOggWithPayload(byte[] packet)
    {
        if (packet.Length > 255)
        {
            throw new ArgumentException("Test helper only supports single-segment pages up to 255 bytes.");
        }

        byte[] buf = new byte[27 + 1 + packet.Length];
        buf[0] = (byte)'O'; buf[1] = (byte)'g'; buf[2] = (byte)'g'; buf[3] = (byte)'S';
        buf[4] = 0;
        buf[5] = 0x02;
        buf[26] = 1;
        buf[27] = (byte)packet.Length;
        Array.Copy(packet, 0, buf, 28, packet.Length);
        return buf;
    }
}
