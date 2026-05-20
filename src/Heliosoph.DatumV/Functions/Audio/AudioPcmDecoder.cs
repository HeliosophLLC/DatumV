using Sdcb.FFmpeg.Codecs;
using Sdcb.FFmpeg.Formats;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Utils;

namespace Heliosoph.DatumV.Functions.Audio;

/// <summary>
/// FFmpeg-backed PCM decoder. Takes an encoded audio container blob
/// (WAV / MP3 / FLAC / OGG / M4A — anything FFmpeg's stock decoders
/// recognise) and emits a flat <c>Float32[]</c> of PCM samples at the
/// caller's requested sample rate.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Mono only (v1).</strong> Stereo and multi-channel sources
/// raise a clear error instead of being auto-downmixed. ML audio
/// pipelines almost universally consume mono; surfacing the channel
/// mismatch explicitly avoids silent transformations that surprise
/// users later. A downmix primitive can land separately when a real
/// consumer needs it.
/// </para>
/// <para>
/// <strong>Resampling.</strong> Uses libswresample (swr_*) to convert
/// the source sample format and rate to <c>AV_SAMPLE_FMT_FLT</c> (32-
/// bit interleaved float) at the caller's target rate. Any
/// container/sample-rate combination FFmpeg supports is converted in
/// one pass.
/// </para>
/// <para>
/// <strong>Lifetime.</strong> Stateless helper — every call opens a
/// fresh FormatContext + CodecContext + SwrContext and disposes them
/// before returning. For per-frame inference loops that call this
/// once per row, the open-cost dominates over the resample itself;
/// if that becomes a real bottleneck, a registry analogous to
/// <c>VideoRegistry</c> can cache warm decoders keyed on the audio
/// blob hash. Not worth it until a profile says so.
/// </para>
/// </remarks>
public static class AudioPcmDecoder
{
    /// <summary>
    /// Decodes <paramref name="audioBytes"/> to a flat Float32 buffer
    /// of PCM samples at <paramref name="targetRate"/> Hz, mono.
    /// Source channel counts &gt; 1 raise <see cref="InvalidOperationException"/> —
    /// callers downmix explicitly via <c>audio_to_mono</c> first.
    /// </summary>
    public static float[] DecodeMonoFloat32(byte[] audioBytes, int targetRate)
    {
        if (targetRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetRate), targetRate,
                "Sample rate must be > 0.");
        }
        return DecodeFloat32Internal(audioBytes, targetRate, requireMonoSource: true, out _);
    }

    /// <summary>
    /// Decodes <paramref name="audioBytes"/> to a mono Float32 buffer at
    /// the source's native sample rate. Auto-downmixes multi-channel
    /// sources via libswresample's default channel mixer (the same
    /// downmix every reference ML audio pipeline uses). Surfaces the
    /// detected source rate through <paramref name="sourceRate"/> so
    /// callers can preserve it when re-encoding (e.g. <c>audio_to_mono</c>
    /// emits a WAV at the original rate, not 16 kHz).
    /// </summary>
    public static float[] DecodeDownmixedFloat32(byte[] audioBytes, out int sourceRate)
        => DecodeFloat32Internal(audioBytes, targetRate: null, requireMonoSource: false, out sourceRate);

    private static float[] DecodeFloat32Internal(
        byte[] audioBytes, int? targetRate, bool requireMonoSource, out int sourceRate)
    {
        ArgumentNullException.ThrowIfNull(audioBytes);
        if (audioBytes.Length == 0)
        {
            sourceRate = 0;
            return Array.Empty<float>();
        }

        using MemoryStream sourceStream = new(audioBytes, writable: false);
        using IOContext io = IOContext.ReadStream(sourceStream, bufferSize: 32 * 1024);
        using FormatContext fc = FormatContext.OpenInputIO(io);
        fc.LoadStreamInfo();

        MediaStream? audioStreamOpt = fc.FindBestStreamOrNull(AVMediaType.Audio);
        if (audioStreamOpt is null)
        {
            throw new InvalidOperationException(
                "Audio source has no audio stream FFmpeg can decode.");
        }
        MediaStream audioStream = audioStreamOpt.Value;
        CodecParameters codecpar = audioStream.Codecpar!;

        int srcChannels = codecpar.ChLayout.nb_channels;
        if (requireMonoSource && srcChannels != 1)
        {
            throw new InvalidOperationException(
                $"audio_samples requires a mono source; this audio has {srcChannels} "
                + "channel(s). Pipe through `audio_to_mono(...)` first to downmix, "
                + "then pass the result to audio_samples.");
        }

        Codec codec = Codec.FindDecoderById(codecpar.CodecId);
        using CodecContext codecCtx = new(codec);
        codecCtx.FillParameters(codecpar);
        codecCtx.Open(codec);

        sourceRate = codecCtx.SampleRate;
        int effectiveTargetRate = targetRate ?? sourceRate;
        return RunDecodeLoop(fc, codecCtx, audioStream, effectiveTargetRate);
    }

    private static unsafe float[] RunDecodeLoop(
        FormatContext fc,
        CodecContext codecCtx,
        MediaStream audioStream,
        int targetRate)
    {
        // Source layout, format, and rate come off the codec context after
        // open. Destination is mono / float / target_rate by contract.
        AVChannelLayout srcLayout = codecCtx.ChLayout;
        AVSampleFormat srcFormat = codecCtx.SampleFormat;
        int srcRate = codecCtx.SampleRate;

        AVChannelLayout dstLayout = default;
        ffmpeg.av_channel_layout_default(&dstLayout, 1);

        // swr_alloc_set_opts2 returns 0 on success, negative on failure.
        // It both allocates the SwrContext and configures it in one call.
        SwrContext* swr = null;
        try
        {
            int allocResult = ffmpeg.swr_alloc_set_opts2(
                &swr,
                &dstLayout, AVSampleFormat.Flt, targetRate,
                &srcLayout, srcFormat, srcRate,
                log_offset: 0, log_ctx: null);
            if (allocResult < 0 || swr is null)
            {
                throw new InvalidOperationException(
                    $"swr_alloc_set_opts2 failed (code {allocResult}).");
            }
            int initResult = ffmpeg.swr_init(swr);
            if (initResult < 0)
            {
                throw new InvalidOperationException(
                    $"swr_init failed (code {initResult}).");
            }

            // Growing buffer for the converted PCM. We don't know the
            // final length up front (compressed durations only estimate);
            // start with a generous prealloc based on the estimated
            // duration so the common case never reallocates.
            long estimatedSamples = EstimateOutputSamples(fc, audioStream, targetRate);
            int initialCapacity = checked((int)Math.Min(estimatedSamples + 1024, int.MaxValue / 2));
            List<float> output = new(initialCapacity);

            using Packet packet = new();
            using Frame decoded = new();

            while (true)
            {
                packet.Unref();
                CodecResult readResult = fc.ReadFrame(packet);
                if (readResult == CodecResult.EOF) break;
                if (packet.StreamIndex != audioStream.Index) continue;

                codecCtx.SendPacket(packet);
                DrainDecodedFrames(codecCtx, decoded, swr, output);
            }

            // EOF: flush the codec, then the resampler.
            ffmpeg.avcodec_send_packet(codecCtx, null);
            DrainDecodedFrames(codecCtx, decoded, swr, output);
            FlushResampler(swr, output);

            return output.ToArray();
        }
        finally
        {
            if (swr is not null)
            {
                ffmpeg.swr_free(&swr);
            }
            ffmpeg.av_channel_layout_uninit(&dstLayout);
        }
    }

    private static unsafe void DrainDecodedFrames(
        CodecContext codecCtx, Frame decoded, SwrContext* swr, List<float> output)
    {
        while (true)
        {
            CodecResult recv = codecCtx.ReceiveFrame(decoded);
            if (recv == CodecResult.Again || recv == CodecResult.EOF) return;
            ConvertFrameToOutput(swr, decoded, output);
        }
    }

    private static unsafe void ConvertFrameToOutput(
        SwrContext* swr, Frame frame, List<float> output)
    {
        int srcSamples = frame.NbSamples;
        // swr_get_out_samples accounts for any input buffered in the
        // resampler from a prior partial conversion (rate mismatch +
        // sub-sample boundaries leave residue). Underestimating risks
        // dropping samples; overestimating is harmless.
        int outSamples = ffmpeg.swr_get_out_samples(swr, srcSamples);
        if (outSamples <= 0) return;

        // Mono Float32 destination — single channel × out_samples × 4 bytes.
        int bytesNeeded = outSamples * sizeof(float);
        byte[] dstManaged = new byte[bytesNeeded];
        fixed (byte* dstPtr = dstManaged)
        {
            byte* dstPlane = dstPtr;
            int converted = ffmpeg.swr_convert(swr,
                &dstPlane, outSamples,
                frame.ExtendedData, srcSamples);
            if (converted < 0)
            {
                throw new InvalidOperationException(
                    $"swr_convert failed (code {converted}).");
            }
            int producedBytes = converted * sizeof(float);
            AppendFloats(dstManaged.AsSpan(0, producedBytes), output);
        }
    }

    private static unsafe void FlushResampler(SwrContext* swr, List<float> output)
    {
        // Pump the resampler with NULL input until it stops producing.
        // Any samples buffered from rate-mismatch residue come out here.
        while (true)
        {
            int pending = ffmpeg.swr_get_out_samples(swr, 0);
            if (pending <= 0) break;

            int bytesNeeded = pending * sizeof(float);
            byte[] dstManaged = new byte[bytesNeeded];
            fixed (byte* dstPtr = dstManaged)
            {
                byte* dstPlane = dstPtr;
                int converted = ffmpeg.swr_convert(swr, &dstPlane, pending, null, 0);
                if (converted <= 0) break;
                AppendFloats(dstManaged.AsSpan(0, converted * sizeof(float)), output);
            }
        }
    }

    private static void AppendFloats(ReadOnlySpan<byte> bytes, List<float> output)
    {
        ReadOnlySpan<float> floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes);
        output.EnsureCapacity(output.Count + floats.Length);
        for (int i = 0; i < floats.Length; i++) output.Add(floats[i]);
    }

    private static long EstimateOutputSamples(FormatContext fc, MediaStream audioStream, int targetRate)
    {
        // Prefer the per-stream duration; fall back to the container's
        // overall duration; final fallback is a small but non-zero hint
        // so the List<T> doesn't reallocate from default capacity 4 on
        // every short clip.
        long durationTicks = 0;
        if (audioStream.Duration != ffmpeg.AV_NOPTS_VALUE && audioStream.TimeBase.Num > 0)
        {
            durationTicks = audioStream.Duration * TimeSpan.TicksPerSecond * audioStream.TimeBase.Num / audioStream.TimeBase.Den;
        }
        if (durationTicks <= 0)
        {
            long fcDuration = fc.Duration;
            if (fcDuration > 0)
            {
                // FormatContext.Duration is in AV_TIME_BASE units (1e6/sec).
                durationTicks = fcDuration * TimeSpan.TicksPerSecond / ffmpeg.AV_TIME_BASE;
            }
        }
        if (durationTicks <= 0) return 16_000;

        double seconds = durationTicks / (double)TimeSpan.TicksPerSecond;
        return (long)(seconds * targetRate);
    }
}
