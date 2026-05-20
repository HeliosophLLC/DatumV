using System.Buffers.Binary;

using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Audio;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Audio;

/// <summary>
/// <c>audio_waveform_envelope(audio, bins)</c>: folds decoded PCM into a
/// per-bin <c>(min, max)</c> Float32 envelope. Exercises shape, value range,
/// null propagation, and bin-count edge cases.
/// </summary>
public sealed class AudioWaveformEnvelopeFunctionTests : ServiceTestBase
{
    [Fact]
    public void ValidateArguments_ReturnsFloat32Array()
    {
        AudioWaveformEnvelopeFunction fn = new();
        DataKind kind = ((IScalarFunction)fn).ValidateArguments([DataKind.Audio, DataKind.Int32]);
        Assert.Equal(DataKind.Float32, kind);
    }

    [Fact]
    public async Task NullAudio_ReturnsNullArray()
    {
        EvaluationFrame frame = CreateEvaluationFrame();
        ValueRef result = await new AudioWaveformEnvelopeFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.Audio), ValueRef.FromInt32(64) },
            frame, default);

        Assert.True(result.IsNull);
        Assert.True(result.IsArray);
        Assert.Equal(DataKind.Float32, result.Kind);
    }

    [Fact]
    public async Task NullBins_ReturnsNullArray()
    {
        EvaluationFrame frame = CreateEvaluationFrame();
        ValueRef audio = await DecodeAudioAsync(BuildMonoSineWave(16000, 16000, 440f), frame);

        ValueRef result = await new AudioWaveformEnvelopeFunction().ExecuteAsync(
            new[] { audio, ValueRef.Null(DataKind.Int32) },
            frame, default);

        Assert.True(result.IsNull);
        Assert.True(result.IsArray);
    }

    [Fact]
    public async Task ZeroBins_Throws()
    {
        EvaluationFrame frame = CreateEvaluationFrame();
        ValueRef audio = await DecodeAudioAsync(BuildMonoSineWave(16000, 16000, 440f), frame);

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new AudioWaveformEnvelopeFunction().ExecuteAsync(
                new[] { audio, ValueRef.FromInt32(0) },
                frame, default));
        Assert.Contains("bins", ex.Message);
    }

    [Fact]
    public async Task NegativeBins_Throws()
    {
        EvaluationFrame frame = CreateEvaluationFrame();
        ValueRef audio = await DecodeAudioAsync(BuildMonoSineWave(16000, 16000, 440f), frame);

        await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new AudioWaveformEnvelopeFunction().ExecuteAsync(
                new[] { audio, ValueRef.FromInt32(-1) },
                frame, default));
    }

    [Fact]
    public async Task SineWave_ProducesEnvelopeWithShapeBinsBy2()
    {
        EvaluationFrame frame = CreateEvaluationFrame();
        ValueRef audio = await DecodeAudioAsync(BuildMonoSineWave(16000, 16000, 440f), frame);
        const int bins = 128;

        ValueRef result = await new AudioWaveformEnvelopeFunction().ExecuteAsync(
            new[] { audio, ValueRef.FromInt32(bins) },
            frame, default);

        Assert.True(result.IsArray);
        Assert.Equal(DataKind.Float32, result.Kind);

        DataValue value = result.ToDataValue(frame.Source);
        Assert.True(value.IsMultiDim);
        Assert.Equal([bins, 2], value.GetShape(frame.Source, frame.SidecarRegistry).ToArray());
    }

    [Fact]
    public async Task SineWave_MinAndMaxAreOppositeSign_AndNearFullScale()
    {
        // A loud 440 Hz sine at amplitude 30000/32768 ≈ 0.92 in Float32 PCM.
        // Each bin holds many cycles → min should be ≈ -0.92, max ≈ +0.92.
        EvaluationFrame frame = CreateEvaluationFrame();
        ValueRef audio = await DecodeAudioAsync(BuildMonoSineWave(16000, 16000, 440f), frame);
        const int bins = 32;

        ValueRef result = await new AudioWaveformEnvelopeFunction().ExecuteAsync(
            new[] { audio, ValueRef.FromInt32(bins) },
            frame, default);

        DataValue value = result.ToDataValue(frame.Source);
        ReadOnlySpan<float> envelope = value.AsArraySpan<float>(frame.Source, frame.SidecarRegistry);

        // Walk all bins — every bin contains many sine periods so each pair
        // should bracket zero with magnitudes near the source amplitude.
        for (int b = 0; b < bins; b++)
        {
            float lo = envelope[b * 2 + 0];
            float hi = envelope[b * 2 + 1];
            Assert.True(lo < 0, $"bin {b} min should be negative; got {lo}");
            Assert.True(hi > 0, $"bin {b} max should be positive; got {hi}");
            Assert.InRange(MathF.Abs(lo), 0.7f, 1.0f);
            Assert.InRange(hi, 0.7f, 1.0f);
        }
    }

    [Fact]
    public async Task Silence_AllBinsAreZero()
    {
        EvaluationFrame frame = CreateEvaluationFrame();
        // BuildMonoSineWave with frequency 0 → all samples are sin(0) = 0 (silence).
        // Header-zero data section in BuildWavHeader already gives silence too,
        // but the explicit sine-at-0 keeps the helper signatures consistent.
        ValueRef audio = await DecodeAudioAsync(BuildMonoSilence(16000, 8000), frame);
        const int bins = 16;

        ValueRef result = await new AudioWaveformEnvelopeFunction().ExecuteAsync(
            new[] { audio, ValueRef.FromInt32(bins) },
            frame, default);

        DataValue value = result.ToDataValue(frame.Source);
        ReadOnlySpan<float> envelope = value.AsArraySpan<float>(frame.Source, frame.SidecarRegistry);

        for (int i = 0; i < envelope.Length; i++)
        {
            Assert.Equal(0f, envelope[i]);
        }
    }

    [Fact]
    public async Task MoreBinsThanSamples_MostBinsAreEmptyZeros()
    {
        // 4 source samples × 32 bins → with the floor-based bin span math,
        // at most 4 bins can receive a sample; the rest must read as the
        // empty-bin sentinel (0, 0).
        EvaluationFrame frame = CreateEvaluationFrame();
        ValueRef audio = await DecodeAudioAsync(BuildMonoSineWave(16000, 4, 440f), frame);
        const int bins = 32;

        ValueRef result = await new AudioWaveformEnvelopeFunction().ExecuteAsync(
            new[] { audio, ValueRef.FromInt32(bins) },
            frame, default);

        DataValue value = result.ToDataValue(frame.Source);
        ReadOnlySpan<float> envelope = value.AsArraySpan<float>(frame.Source, frame.SidecarRegistry);

        int nonEmptyBins = 0;
        for (int b = 0; b < bins; b++)
        {
            if (envelope[b * 2 + 0] != 0f || envelope[b * 2 + 1] != 0f)
            {
                nonEmptyBins++;
            }
        }
        // Resampler can stretch the decoded sample count slightly, but
        // we should never see more non-empty bins than we'd expect from
        // a tiny clip projected onto 32 bins.
        Assert.True(nonEmptyBins <= 8,
            $"expected most bins to be empty with only 4 source samples; got {nonEmptyBins} non-empty.");
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static async Task<ValueRef> DecodeAudioAsync(byte[] wavBytes, EvaluationFrame frame)
    {
        // Wrap the WAV blob as an Audio value via the public audio_decode path
        // so tests share the same construction route as user SQL.
        return await new AudioDecodeFunction().ExecuteAsync(
            new[] { ValueRef.FromBytes(DataKind.UInt8, wavBytes, isArray: true) },
            frame, default);
    }

    private static byte[] BuildMonoSineWave(uint sampleRate, uint frameCount, float frequencyHz)
    {
        const ushort channels = 1;
        const ushort bitsPerSample = 16;
        ushort blockAlign = (ushort)(channels * (bitsPerSample / 8));
        uint dataBytes = frameCount * blockAlign;

        byte[] wav = BuildWavHeader(sampleRate, channels, bitsPerSample, dataBytes);
        int sampleOffset = wav.Length - (int)dataBytes;
        double phaseStep = 2.0 * Math.PI * frequencyHz / sampleRate;
        for (uint i = 0; i < frameCount; i++)
        {
            short sample = (short)(Math.Sin(i * phaseStep) * 30000);
            BinaryPrimitives.WriteInt16LittleEndian(
                wav.AsSpan(sampleOffset + (int)i * 2, 2), sample);
        }
        return wav;
    }

    private static byte[] BuildMonoSilence(uint sampleRate, uint frameCount)
    {
        const ushort channels = 1;
        const ushort bitsPerSample = 16;
        ushort blockAlign = (ushort)(channels * (bitsPerSample / 8));
        uint dataBytes = frameCount * blockAlign;
        return BuildWavHeader(sampleRate, channels, bitsPerSample, dataBytes);
    }

    private static byte[] BuildWavHeader(uint sampleRate, ushort channels, ushort bitsPerSample, uint dataBytes)
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

    private static void WriteAscii(byte[] buf, ref int cursor, string ascii)
    {
        for (int i = 0; i < ascii.Length; i++) buf[cursor++] = (byte)ascii[i];
    }
}
