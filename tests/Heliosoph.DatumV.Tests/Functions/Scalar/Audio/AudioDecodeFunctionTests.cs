using System.Buffers.Binary;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Audio;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Audio;

/// <summary>
/// <c>audio_decode(bytes)</c> scalar: wraps an encoded audio byte array as
/// a typed <see cref="DataKind.Audio"/> value with header-parsed inline
/// metadata. Validates schema declaration, the NULL pass-through, and the
/// WAV + FLAC metadata round-trips on the materialization boundary.
/// </summary>
public sealed class AudioDecodeFunctionTests : ServiceTestBase
{
    [Fact]
    public void ValidateArguments_ReturnsAudio()
    {
        AudioDecodeFunction fn = new();
        DataKind kind = ((IScalarFunction)fn).ValidateArguments([DataKind.UInt8]);
        Assert.Equal(DataKind.Audio, kind);
    }

    [Fact]
    public async Task AudioDecode_NullBytes_ReturnsNullAudio()
    {
        ValueRef result = await new AudioDecodeFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.UInt8) },
            CreateEvaluationFrame(), default);

        Assert.Equal(DataKind.Audio, result.Kind);
        Assert.True(result.IsNull);
    }

    [Fact]
    public async Task AudioDecode_WavBytes_ProducesAudioWithParsedMetadata()
    {
        // 22.05 kHz mono 16-bit, 1 s of silence.
        byte[] wav = BuildWave(sampleRate: 22050, channels: 1, bitsPerSample: 16, dataBytes: 44100);

        ValueRef result = await new AudioDecodeFunction().ExecuteAsync(
            new[] { ValueRef.FromBytes(DataKind.UInt8, wav, isArray: true) },
            CreateEvaluationFrame(), default);

        Assert.Equal(DataKind.Audio, result.Kind);

        // Metadata is stamped at the materialization boundary (ToDataValue), not
        // inside ExecuteAsync — verify by walking through ToDataValue and reading
        // the inline accessors on the resulting DataValue.
        using Heliosoph.DatumV.Model.Arena store = CreateArena();
        DataValue audio = result.ToDataValue(store);
        Assert.Equal(DataKind.Audio, audio.Kind);
        Assert.Equal(22050u, audio.AudioSampleRate);
        Assert.Equal((byte)1, audio.AudioChannels);
        Assert.Equal((byte)16, audio.AudioBitDepth);
        Assert.Equal(22050u, audio.AudioFrameCount);
    }

    [Fact]
    public async Task AudioDecode_FlacBytes_ProducesAudioWithParsedMetadata()
    {
        // LibriSpeech-shaped FLAC: 16 kHz mono 16-bit, ~2 s of audio.
        byte[] flac = BuildFlac(sampleRate: 16000, channels: 1, bitsPerSample: 16, totalSamples: 32000);

        ValueRef result = await new AudioDecodeFunction().ExecuteAsync(
            new[] { ValueRef.FromBytes(DataKind.UInt8, flac, isArray: true) },
            CreateEvaluationFrame(), default);

        using Heliosoph.DatumV.Model.Arena store = CreateArena();
        DataValue audio = result.ToDataValue(store);
        Assert.Equal(DataKind.Audio, audio.Kind);
        Assert.Equal(16000u, audio.AudioSampleRate);
        Assert.Equal((byte)1, audio.AudioChannels);
        Assert.Equal((byte)16, audio.AudioBitDepth);
        Assert.Equal(32000u, audio.AudioFrameCount);
    }

    [Fact]
    public async Task AudioDecode_UnknownFormatBytes_StillProducesAudioWithZeroMetadata()
    {
        // Some random bytes that aren't a known audio format. The function shouldn't
        // throw — it should produce a valid Audio value with zero-sentinel metadata,
        // matching audio_sample_rate() → NULL semantics for unparseable inputs.
        byte[] mystery = new byte[256];
        mystery[0] = 0xFF;

        ValueRef result = await new AudioDecodeFunction().ExecuteAsync(
            new[] { ValueRef.FromBytes(DataKind.UInt8, mystery, isArray: true) },
            CreateEvaluationFrame(), default);

        using Heliosoph.DatumV.Model.Arena store = CreateArena();
        DataValue audio = result.ToDataValue(store);
        Assert.Equal(DataKind.Audio, audio.Kind);
        Assert.Equal(0u, audio.AudioSampleRate);
        Assert.Equal((byte)0, audio.AudioChannels);
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static byte[] BuildWave(uint sampleRate, ushort channels, ushort bitsPerSample, uint dataBytes)
    {
        const int fmtPayload = 16;
        int total = 12 + 8 + fmtPayload + 8 + (int)dataBytes;
        byte[] buf = new byte[total];
        int cursor = 0;

        WriteAscii(buf, ref cursor, "RIFF");
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(cursor, 4), (uint)(total - 8)); cursor += 4;
        WriteAscii(buf, ref cursor, "WAVE");

        WriteAscii(buf, ref cursor, "fmt ");
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(cursor, 4), (uint)fmtPayload); cursor += 4;
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
        return buf;
    }

    private static byte[] BuildFlac(uint sampleRate, byte channels, byte bitsPerSample, ulong totalSamples)
    {
        byte[] buf = new byte[42];
        buf[0] = (byte)'f'; buf[1] = (byte)'L'; buf[2] = (byte)'a'; buf[3] = (byte)'C';
        buf[4] = 0x80;
        buf[5] = 0x00; buf[6] = 0x00; buf[7] = 0x22;
        buf[8] = 0x10; buf[9] = 0x00;
        buf[10] = 0x10; buf[11] = 0x00;

        ulong packed = ((ulong)(sampleRate & 0xF_FFFFu) << 44)
                     | ((ulong)((channels - 1) & 0x7u) << 41)
                     | ((ulong)((bitsPerSample - 1) & 0x1Fu) << 36)
                     | (totalSamples & 0xF_FFFF_FFFFUL);
        BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(18, 8), packed);
        return buf;
    }

    private static void WriteAscii(byte[] buf, ref int cursor, string ascii)
    {
        for (int i = 0; i < ascii.Length; i++) buf[cursor++] = (byte)ascii[i];
    }
}
