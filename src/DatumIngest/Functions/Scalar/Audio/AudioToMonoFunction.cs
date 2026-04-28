using System.Buffers.Binary;
using DatumIngest.Execution;
using DatumIngest.Functions.Audio;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Audio;

/// <summary>
/// <c>audio_to_mono(audio Audio) → Audio</c>. Decodes the source audio,
/// downmixes any channel count to a single mono channel via libswresample's
/// default mixer, and re-encodes as a 16-bit PCM WAV at the source's
/// native sample rate.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a separate primitive.</strong> <c>audio_samples</c> rejects
/// multi-channel input by design — silent channel transformations are the
/// kind of "Just Works" shortcut that ends up biting users when their
/// downstream model behaves subtly differently than the same model in the
/// reference Python pipeline. <c>audio_to_mono</c> is the explicit
/// channel-downmix step, calling out in the SQL that the source is being
/// rearranged before it hits the model.
/// </para>
/// <para>
/// <strong>Composition.</strong> The canonical stereo-input chain is
/// <c>audio_samples(16000, audio_to_mono(clip))</c>: downmix first
/// (preserves source rate), then resample to whatever the model expects.
/// A mono source flows through as a no-op transcoding pass (decode →
/// re-encode at the same rate); cheap and idempotent.
/// </para>
/// <para>
/// <strong>Output format.</strong> 16-bit signed PCM WAV. Universally
/// supported, matches what <c>AudioHeaderParser</c> already parses for
/// metadata stamping, and survives a round-trip through
/// <c>AudioDataValueFactory.FromEncodedBytes</c> with sample-rate /
/// channel-count / bit-depth populated. Float32 PCM would preserve
/// more dynamic range but isn't worth the format gymnastics here —
/// <c>audio_samples</c> reads back to Float32 anyway.
/// </para>
/// </remarks>
public sealed class AudioToMonoFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "audio_to_mono";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Downmixes the source audio to a single mono channel and re-encodes "
        + "as 16-bit PCM WAV at the source's native sample rate. Pipe through "
        + "this before audio_samples when the source is stereo or multi-"
        + "channel — audio_samples rejects non-mono input by design.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("audio", DataKindMatcher.Exact(DataKind.Audio))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Audio)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<AudioToMonoFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef audioArg = arguments.Span[0];
        if (audioArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.Audio));
        }

        DataValue audioValue = audioArg.ToDataValue(frame.Source);
        byte[] sourceBytes = audioValue.AsAudio(frame.Source, frame.SidecarRegistry);

        float[] monoFloats;
        int sampleRate;
        try
        {
            monoFloats = AudioPcmDecoder.DecodeDownmixedFloat32(sourceBytes, out sampleRate);
        }
        catch (InvalidOperationException ex)
        {
            throw new FunctionArgumentException(Name, ex.Message);
        }

        byte[] wav = EncodeMonoInt16Wav(monoFloats, sampleRate);
        return new ValueTask<ValueRef>(ValueRef.FromBytes(DataKind.Audio, wav));
    }

    /// <summary>
    /// Encodes <paramref name="samples"/> as a minimal 16-bit signed PCM
    /// WAV at <paramref name="sampleRate"/>, mono. Layout: 12-byte RIFF
    /// header → 24-byte fmt chunk → 8-byte data chunk header → PCM payload.
    /// Float values outside [-1.0, 1.0] are clamped to the Int16 range
    /// rather than wrapping; a hot-mic source that briefly exceeds full
    /// scale is more useful as a clipped buffer than as scrambled samples.
    /// </summary>
    private static byte[] EncodeMonoInt16Wav(float[] samples, int sampleRate)
    {
        // Guard against pathological inputs that would underflow the
        // header math (zero-rate, no samples). The simpler "empty WAV"
        // shape is preferable to throwing here — caller already handled
        // the upstream null path.
        if (sampleRate <= 0) sampleRate = 16000;

        const int fmtPayload = 16;
        const ushort channels = 1;
        const ushort bitsPerSample = 16;
        ushort blockAlign = channels * (bitsPerSample / 8);
        uint byteRate = (uint)sampleRate * blockAlign;
        uint dataBytes = (uint)samples.Length * blockAlign;

        int total = 12 + 8 + fmtPayload + 8 + (int)dataBytes;
        byte[] buf = new byte[total];
        int cursor = 0;

        WriteAscii(buf, ref cursor, "RIFF");
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(cursor, 4), (uint)(total - 8)); cursor += 4;
        WriteAscii(buf, ref cursor, "WAVE");

        WriteAscii(buf, ref cursor, "fmt ");
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(cursor, 4), fmtPayload); cursor += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(cursor, 2), 1); cursor += 2;          // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(cursor, 2), channels); cursor += 2;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(cursor, 4), (uint)sampleRate); cursor += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(cursor, 4), byteRate); cursor += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(cursor, 2), blockAlign); cursor += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(cursor, 2), bitsPerSample); cursor += 2;

        WriteAscii(buf, ref cursor, "data");
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(cursor, 4), dataBytes); cursor += 4;

        for (int i = 0; i < samples.Length; i++)
        {
            float v = samples[i];
            short s;
            if (v >= 1.0f) s = short.MaxValue;
            else if (v <= -1.0f) s = short.MinValue;
            else s = (short)MathF.Round(v * 32767f);
            BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(cursor, 2), s);
            cursor += 2;
        }

        return buf;
    }

    private static void WriteAscii(byte[] buf, ref int cursor, string ascii)
    {
        for (int i = 0; i < ascii.Length; i++) buf[cursor++] = (byte)ascii[i];
    }
}
