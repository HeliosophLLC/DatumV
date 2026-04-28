using System.Buffers.Binary;
using DatumIngest.Functions.Audio;

namespace DatumIngest.Tests.Functions.Audio;

/// <summary>
/// FFmpeg-backed PCM decode tests. Exercises mono WAV decode + resample
/// at the chosen rate and confirms the stereo-rejection contract.
/// </summary>
public sealed class AudioPcmDecoderTests
{
    [Fact]
    public void DecodeMonoFloat32_MonoWav16kHz_SameRate_ProducesExpectedSampleCount()
    {
        const uint sampleRate = 16000;
        const uint frameCount = 16000; // 1 second
        byte[] wav = BuildMonoSineWave(sampleRate, frameCount, frequencyHz: 440f);

        float[] samples = AudioPcmDecoder.DecodeMonoFloat32(wav, (int)sampleRate);

        // ±1% tolerance accounts for resampler edge-trim. At source rate
        // there's effectively no resampling work, so we should land very
        // close to the exact frame count.
        Assert.InRange(samples.Length, 15800, 16200);
    }

    [Fact]
    public void DecodeMonoFloat32_MonoWav44_1kHz_Resampled_To16kHz_HasFewerSamples()
    {
        const uint sourceRate = 44100;
        const uint frameCount = 44100; // 1 second
        byte[] wav = BuildMonoSineWave(sourceRate, frameCount, frequencyHz: 440f);

        float[] samples = AudioPcmDecoder.DecodeMonoFloat32(wav, 16000);

        // 1 second resampled to 16 kHz → ~16 000 samples (±1%).
        Assert.InRange(samples.Length, 15800, 16200);
    }

    [Fact]
    public void DecodeMonoFloat32_MonoWav_UpsampledTo48kHz_HasMoreSamples()
    {
        const uint sourceRate = 16000;
        const uint frameCount = 16000; // 1 second
        byte[] wav = BuildMonoSineWave(sourceRate, frameCount, frequencyHz: 440f);

        float[] samples = AudioPcmDecoder.DecodeMonoFloat32(wav, 48000);

        // 1 second upsampled to 48 kHz → ~48 000 samples (±1%).
        Assert.InRange(samples.Length, 47500, 48500);
    }

    [Fact]
    public void DecodeMonoFloat32_StereoSource_Throws()
    {
        // Stereo WAV — should be rejected per the mono-only contract.
        const uint sampleRate = 16000;
        const uint frameCount = 16000;
        byte[] wav = BuildStereoSilenceWave(sampleRate, frameCount);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            AudioPcmDecoder.DecodeMonoFloat32(wav, 16000));
        Assert.Contains("mono", ex.Message);
        Assert.Contains("2 channel", ex.Message);
    }

    [Fact]
    public void DecodeMonoFloat32_EmptyBytes_ReturnsEmpty()
    {
        float[] samples = AudioPcmDecoder.DecodeMonoFloat32(Array.Empty<byte>(), 16000);
        Assert.Empty(samples);
    }

    [Fact]
    public void DecodeMonoFloat32_NonPositiveRate_Throws()
    {
        byte[] wav = BuildMonoSineWave(16000, 16000, 440f);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AudioPcmDecoder.DecodeMonoFloat32(wav, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AudioPcmDecoder.DecodeMonoFloat32(wav, -1));
    }

    // ───────────────────────── Downmix path ─────────────────────────

    [Fact]
    public void DecodeDownmixedFloat32_StereoSource_DownmixesToMonoAtSourceRate()
    {
        const uint sampleRate = 44100;
        const uint frameCount = 44100; // 1 second stereo
        byte[] wav = BuildStereoSineWave(sampleRate, frameCount, frequencyHz: 440f);

        float[] samples = AudioPcmDecoder.DecodeDownmixedFloat32(wav, out int sourceRate);

        Assert.Equal((int)sampleRate, sourceRate);
        // Stereo input downmixed to mono → roughly source frame count (not 2×),
        // ±1% tolerance for resampler edge-trim.
        Assert.InRange(samples.Length, 43600, 44600);
    }

    [Fact]
    public void DecodeDownmixedFloat32_MonoSource_PassesThroughAtSourceRate()
    {
        const uint sampleRate = 22050;
        const uint frameCount = 22050;
        byte[] wav = BuildMonoSineWave(sampleRate, frameCount, frequencyHz: 440f);

        float[] samples = AudioPcmDecoder.DecodeDownmixedFloat32(wav, out int sourceRate);

        Assert.Equal((int)sampleRate, sourceRate);
        Assert.InRange(samples.Length, 21800, 22300);
    }

    [Fact]
    public void DecodeDownmixedFloat32_EmptyBytes_ReturnsEmptyAndZeroRate()
    {
        float[] samples = AudioPcmDecoder.DecodeDownmixedFloat32(Array.Empty<byte>(), out int sourceRate);
        Assert.Empty(samples);
        Assert.Equal(0, sourceRate);
    }

    private static byte[] BuildStereoSineWave(uint sampleRate, uint frameCount, float frequencyHz)
    {
        const ushort channels = 2;
        const ushort bitsPerSample = 16;
        ushort blockAlign = (ushort)(channels * (bitsPerSample / 8));
        uint dataBytes = frameCount * blockAlign;

        byte[] wav = BuildWavHeader(sampleRate, channels, bitsPerSample, dataBytes);
        int sampleOffset = wav.Length - (int)dataBytes;
        double phaseStep = 2.0 * Math.PI * frequencyHz / sampleRate;
        for (uint i = 0; i < frameCount; i++)
        {
            short sample = (short)(Math.Sin(i * phaseStep) * 30000);
            // Interleaved: L, R per frame. Same value on both channels — the
            // downmixer's job stays unambiguous (L+R averaged = sample).
            int baseOffset = sampleOffset + (int)i * 4;
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(baseOffset, 2), sample);
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(baseOffset + 2, 2), sample);
        }
        return wav;
    }

    // ───────────────────────── Helpers ─────────────────────────

    /// <summary>
    /// Builds a 16-bit PCM mono WAV holding a sine wave at
    /// <paramref name="frequencyHz"/>. Carrying a real signal (not just
    /// silence) gives the resampler something non-trivial to chew on,
    /// which is closer to what the function sees in practice.
    /// </summary>
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

    private static byte[] BuildStereoSilenceWave(uint sampleRate, uint frameCount)
    {
        const ushort channels = 2;
        const ushort bitsPerSample = 16;
        ushort blockAlign = (ushort)(channels * (bitsPerSample / 8));
        uint dataBytes = frameCount * blockAlign;
        // BuildWavHeader zero-fills the data region; that's silence in PCM.
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
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(cursor, 2), 1); cursor += 2;          // PCM
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
