using System.Buffers.Binary;
using DatumIngest.Functions.Audio;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Audio;

/// <summary>
/// Round-trip tests for the WAV header parser + the audio inline-metadata stamping path.
/// </summary>
public sealed class AudioHeaderParserTests
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
        using Arena store = new();

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
        using Arena store = new();

        DataValue dv = AudioDataValueFactory.FromEncodedBytes(mystery, store);

        Assert.Equal(DataKind.Audio, dv.Kind);
        Assert.Equal(0u, dv.AudioSampleRate);
        Assert.Equal((byte)0, dv.AudioChannels);
        Assert.Equal((byte)0, dv.AudioBitDepth);
        Assert.Equal(0u, dv.AudioFrameCount);
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
}
