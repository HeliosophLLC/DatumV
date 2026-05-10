using System.Buffers.Binary;

using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Audio;
using DatumIngest.Model;

using SkiaSharp;

namespace DatumIngest.Tests.Functions.Scalar.Audio;

/// <summary>
/// <c>audio_waveform(audio, width, height, options)</c>: end-to-end sugar
/// that decodes the audio and rasterises the peak envelope as
/// Audacity-style vertical bars over a background fill. Exercises image
/// dimensions, fg/bg colour application, null propagation, and the
/// options-struct field-resolution contract.
/// </summary>
public sealed class AudioWaveformFunctionTests : ServiceTestBase
{
    [Fact]
    public void ValidateArguments_ReturnsImage()
    {
        AudioWaveformFunction fn = new();
        DataKind kind = ((IScalarFunction)fn).ValidateArguments(
            [DataKind.Audio, DataKind.Int32, DataKind.Int32, DataKind.Struct]);
        Assert.Equal(DataKind.Image, kind);
    }

    [Fact]
    public async Task NullAudio_ReturnsNullImage()
    {
        EvaluationFrame frame = CreateEvaluationFrame();
        ValueRef result = await new AudioWaveformFunction().ExecuteAsync(
            new[]
            {
                ValueRef.Null(DataKind.Audio),
                ValueRef.FromInt32(100),
                ValueRef.FromInt32(50),
                ValueRef.NullUntypedStruct(),
            },
            frame, default);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Image, result.Kind);
    }

    [Fact]
    public async Task NullOptions_ReturnsNullImage()
    {
        DatumIngest.Execution.ExecutionContext context = CreateExecutionContext();
        EvaluationFrame frame = CreateEvaluationFrame(context);
        ValueRef audio = await DecodeAudioAsync(BuildMonoSineWave(16000, 1600, 440f), frame);

        ValueRef result = await new AudioWaveformFunction().ExecuteAsync(
            new[]
            {
                audio,
                ValueRef.FromInt32(100), ValueRef.FromInt32(50),
                ValueRef.NullUntypedStruct(),
            },
            frame, default);

        Assert.True(result.IsNull);
    }

    [Fact]
    public async Task ZeroWidth_Throws()
    {
        DatumIngest.Execution.ExecutionContext context = CreateExecutionContext();
        EvaluationFrame frame = CreateEvaluationFrame(context);
        ValueRef audio = await DecodeAudioAsync(BuildMonoSineWave(16000, 1600, 440f), frame);
        ValueRef options = MakeOptions(context, fg: new SKColor(255, 0, 0), bg: new SKColor(0, 0, 0));

        await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new AudioWaveformFunction().ExecuteAsync(
                new[]
                {
                    audio, ValueRef.FromInt32(0), ValueRef.FromInt32(50), options,
                },
                frame, default));
    }

    [Fact]
    public async Task ValidAudio_RendersImageWithRequestedDimensions()
    {
        DatumIngest.Execution.ExecutionContext context = CreateExecutionContext();
        EvaluationFrame frame = CreateEvaluationFrame(context);
        ValueRef audio = await DecodeAudioAsync(BuildMonoSineWave(16000, 1600, 440f), frame);
        ValueRef options = MakeOptions(context, fg: new SKColor(255, 255, 255), bg: new SKColor(0, 0, 0));

        ValueRef result = await new AudioWaveformFunction().ExecuteAsync(
            new[] { audio, ValueRef.FromInt32(120), ValueRef.FromInt32(40), options },
            frame, default);

        SKBitmap bitmap = result.AsImage();
        Assert.Equal(120, bitmap.Width);
        Assert.Equal(40, bitmap.Height);
    }

    [Fact]
    public async Task BackgroundColor_AppearsAtCornerPixels()
    {
        // Quiet (silent) audio: no waveform drawn, every pixel should be bg.
        DatumIngest.Execution.ExecutionContext context = CreateExecutionContext();
        EvaluationFrame frame = CreateEvaluationFrame(context);
        ValueRef audio = await DecodeAudioAsync(BuildMonoSilence(16000, 1600), frame);
        ValueRef options = MakeOptions(context,
            fg: new SKColor(255, 255, 255),
            bg: new SKColor(0, 0, 255));

        ValueRef result = await new AudioWaveformFunction().ExecuteAsync(
            new[] { audio, ValueRef.FromInt32(80), ValueRef.FromInt32(40), options },
            frame, default);

        SKBitmap bitmap = result.AsImage();
        // Top-left and bottom-right corners.
        SKColor tl = bitmap.GetPixel(0, 0);
        SKColor br = bitmap.GetPixel(79, 39);
        Assert.Equal((byte)0, tl.Red);
        Assert.Equal((byte)0, tl.Green);
        Assert.Equal((byte)255, tl.Blue);
        Assert.Equal((byte)0, br.Red);
        Assert.Equal((byte)0, br.Green);
        Assert.Equal((byte)255, br.Blue);
    }

    [Fact]
    public async Task ForegroundColor_AppearsForLoudSignal_AtCentreline()
    {
        // A loud sine spans the full amplitude range; in every column the
        // bin's (min, max) span crosses the centreline, so the vertical
        // stroke must paint at least one foreground pixel at y = height/2.
        DatumIngest.Execution.ExecutionContext context = CreateExecutionContext();
        EvaluationFrame frame = CreateEvaluationFrame(context);
        ValueRef audio = await DecodeAudioAsync(BuildMonoSineWave(16000, 16000, 440f), frame);
        ValueRef options = MakeOptions(context,
            fg: new SKColor(255, 0, 0),
            bg: new SKColor(0, 0, 0));

        const int width = 80, height = 40;
        ValueRef result = await new AudioWaveformFunction().ExecuteAsync(
            new[] { audio, ValueRef.FromInt32(width), ValueRef.FromInt32(height), options },
            frame, default);

        SKBitmap bitmap = result.AsImage();
        int redPixelsOnCentreline = 0;
        int midY = height / 2;
        for (int x = 0; x < width; x++)
        {
            SKColor px = bitmap.GetPixel(x, midY);
            if (px.Red == 255 && px.Green == 0 && px.Blue == 0)
            {
                redPixelsOnCentreline++;
            }
        }
        // A loud sine should mark the centreline at most columns; we accept
        // any clear majority so the test isn't brittle against Skia's
        // sub-pixel stroke rasterisation.
        Assert.True(redPixelsOnCentreline >= width / 2,
            $"expected most centreline pixels to be foreground; only {redPixelsOnCentreline}/{width} matched.");
    }

    [Fact]
    public async Task OptionsStructMissingFields_Throws()
    {
        DatumIngest.Execution.ExecutionContext context = CreateExecutionContext();
        EvaluationFrame frame = CreateEvaluationFrame(context);
        ValueRef audio = await DecodeAudioAsync(BuildMonoSineWave(16000, 1600, 440f), frame);

        // Build a struct with the wrong field names.
        StructFieldDescriptor[] wrongFields =
        [
            new StructFieldDescriptor("foreground", context.Types!.InternScalarType(DataKind.Color)),
            new StructFieldDescriptor("background", context.Types!.InternScalarType(DataKind.Color)),
        ];
        int wrongTypeId = context.Types!.InternStructType(wrongFields);
        ValueRef wrongOptions = ValueRef.FromStruct(
            [ValueRef.FromColor(255, 0, 0, 255), ValueRef.FromColor(0, 0, 0, 255)],
            (ushort)wrongTypeId);

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new AudioWaveformFunction().ExecuteAsync(
                new[] { audio, ValueRef.FromInt32(80), ValueRef.FromInt32(40), wrongOptions },
                frame, default));
        Assert.Contains("fg", ex.Message);
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static ValueRef MakeOptions(
        DatumIngest.Execution.ExecutionContext context, SKColor fg, SKColor bg)
    {
        StructFieldDescriptor[] fields =
        [
            new StructFieldDescriptor("fg", context.Types!.InternScalarType(DataKind.Color)),
            new StructFieldDescriptor("bg", context.Types!.InternScalarType(DataKind.Color)),
        ];
        int typeId = context.Types!.InternStructType(fields);
        return ValueRef.FromStruct(
            new[]
            {
                ValueRef.FromColor(fg.Red, fg.Green, fg.Blue, fg.Alpha),
                ValueRef.FromColor(bg.Red, bg.Green, bg.Blue, bg.Alpha),
            },
            (ushort)typeId);
    }

    private static async Task<ValueRef> DecodeAudioAsync(byte[] wavBytes, EvaluationFrame frame)
    {
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
