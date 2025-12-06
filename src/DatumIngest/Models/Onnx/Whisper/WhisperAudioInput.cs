namespace DatumIngest.Models.Onnx.Whisper;

/// <summary>
/// Decodes WAV bytes into the mono 16kHz float samples Whisper expects.
/// Handles the subset of WAV configurations the model zoo actually
/// produces: 16-bit PCM, IEEE float, and mono/stereo at common sample
/// rates (16k / 22.05k / 24k / 44.1k / 48k). Stereo is downmixed to mono;
/// non-16k input is resampled with linear interpolation.
/// </summary>
/// <remarks>
/// <para>
/// Linear-interpolation resampling is good enough for STT on speech —
/// Whisper is robust to mild aliasing. If you ever route truly low-fidelity
/// 8kHz telephony audio in, an upsampler with a low-pass filter would help
/// noticeably; for now the simpler implementation matches the demo cadence.
/// </para>
/// </remarks>
internal static class WhisperAudioInput
{
    /// <summary>
    /// Parses the WAV bytes, downmixes to mono, and resamples to 16kHz.
    /// Throws <see cref="InvalidDataException"/> on malformed headers or
    /// unsupported subformats (e.g. compressed WAVs).
    /// </summary>
    public static float[] DecodeToMono16k(ReadOnlySpan<byte> wavBytes)
    {
        ParseWavHeader(wavBytes, out int sampleRate, out int channels, out int bitsPerSample,
                       out int audioFormat, out int dataOffset, out int dataLength);

        float[] interleaved = audioFormat switch
        {
            // PCM16
            1 when bitsPerSample == 16 => DecodePcm16(wavBytes, dataOffset, dataLength),
            // PCM8 (unsigned)
            1 when bitsPerSample == 8 => DecodePcm8(wavBytes, dataOffset, dataLength),
            // PCM24
            1 when bitsPerSample == 24 => DecodePcm24(wavBytes, dataOffset, dataLength),
            // IEEE float32
            3 when bitsPerSample == 32 => DecodeFloat32(wavBytes, dataOffset, dataLength),
            _ => throw new InvalidDataException(
                $"Unsupported WAV format: audioFormat={audioFormat}, bitsPerSample={bitsPerSample}. " +
                "Whisper accepts PCM 8/16/24-bit or IEEE float32; transcode unusual formats first."),
        };

        float[] mono = channels switch
        {
            1 => interleaved,
            2 => DownmixStereoToMono(interleaved),
            _ => DownmixToMono(interleaved, channels),
        };

        if (sampleRate == WhisperMelSpectrogram.SampleRate)
        {
            return mono;
        }

        return Resample(mono, sampleRate, WhisperMelSpectrogram.SampleRate);
    }

    private static void ParseWavHeader(
        ReadOnlySpan<byte> wav,
        out int sampleRate,
        out int channels,
        out int bitsPerSample,
        out int audioFormat,
        out int dataOffset,
        out int dataLength)
    {
        if (wav.Length < 44)
        {
            throw new InvalidDataException("WAV file too short for a valid header (need ≥44 bytes).");
        }

        // RIFF/WAVE/fmt header at fixed offsets.
        if (wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F')
        {
            throw new InvalidDataException("Not a RIFF file (missing 'RIFF' magic).");
        }
        if (wav[8] != 'W' || wav[9] != 'A' || wav[10] != 'V' || wav[11] != 'E')
        {
            throw new InvalidDataException("Not a WAVE file (missing 'WAVE' identifier).");
        }

        // Walk subchunks starting at offset 12. Each is 8-byte header + payload.
        // We expect 'fmt ' first (canonical) and 'data' eventually; some writers
        // interleave 'LIST' or 'fact' chunks between them, so we scan rather
        // than assuming fixed offsets beyond 12.
        int offset = 12;
        audioFormat = -1;
        channels = -1;
        sampleRate = -1;
        bitsPerSample = -1;
        dataOffset = -1;
        dataLength = -1;

        while (offset + 8 <= wav.Length)
        {
            ReadOnlySpan<byte> id = wav.Slice(offset, 4);
            int chunkSize = ReadInt32LE(wav, offset + 4);
            int payloadStart = offset + 8;

            if (id[0] == 'f' && id[1] == 'm' && id[2] == 't' && id[3] == ' ')
            {
                if (chunkSize < 16)
                {
                    throw new InvalidDataException($"'fmt ' chunk too small ({chunkSize} bytes).");
                }
                audioFormat   = ReadInt16LE(wav, payloadStart + 0);
                channels      = ReadInt16LE(wav, payloadStart + 2);
                sampleRate    = ReadInt32LE(wav, payloadStart + 4);
                bitsPerSample = ReadInt16LE(wav, payloadStart + 14);
            }
            else if (id[0] == 'd' && id[1] == 'a' && id[2] == 't' && id[3] == 'a')
            {
                dataOffset = payloadStart;
                dataLength = chunkSize;
                break;
            }

            // Subchunks pad to even byte boundary.
            offset = payloadStart + chunkSize + (chunkSize & 1);
        }

        if (audioFormat == -1 || dataOffset == -1)
        {
            throw new InvalidDataException("WAV file missing 'fmt ' or 'data' chunk.");
        }
        if (dataOffset + dataLength > wav.Length)
        {
            // Some writers under-report; clamp to actual buffer length.
            dataLength = wav.Length - dataOffset;
        }
    }

    private static float[] DecodePcm16(ReadOnlySpan<byte> wav, int dataOffset, int dataLength)
    {
        int sampleCount = dataLength / 2;
        float[] result = new float[sampleCount];
        const float scale = 1f / 32768f;
        for (int i = 0; i < sampleCount; i++)
        {
            short s = (short)(wav[dataOffset + 2 * i] | (wav[dataOffset + 2 * i + 1] << 8));
            result[i] = s * scale;
        }
        return result;
    }

    private static float[] DecodePcm8(ReadOnlySpan<byte> wav, int dataOffset, int dataLength)
    {
        // 8-bit WAV is unsigned [0, 255] with 128 = silence.
        float[] result = new float[dataLength];
        const float scale = 1f / 128f;
        for (int i = 0; i < dataLength; i++)
        {
            result[i] = (wav[dataOffset + i] - 128) * scale;
        }
        return result;
    }

    private static float[] DecodePcm24(ReadOnlySpan<byte> wav, int dataOffset, int dataLength)
    {
        int sampleCount = dataLength / 3;
        float[] result = new float[sampleCount];
        const float scale = 1f / 8388608f;  // 2^23
        for (int i = 0; i < sampleCount; i++)
        {
            int p = dataOffset + 3 * i;
            int v = wav[p] | (wav[p + 1] << 8) | (wav[p + 2] << 16);
            // Sign-extend the 24-bit signed value into 32 bits.
            if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
            result[i] = v * scale;
        }
        return result;
    }

    private static float[] DecodeFloat32(ReadOnlySpan<byte> wav, int dataOffset, int dataLength)
    {
        int sampleCount = dataLength / 4;
        float[] result = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            int bits = wav[dataOffset + 4 * i]
                     | (wav[dataOffset + 4 * i + 1] << 8)
                     | (wav[dataOffset + 4 * i + 2] << 16)
                     | (wav[dataOffset + 4 * i + 3] << 24);
            result[i] = BitConverter.Int32BitsToSingle(bits);
        }
        return result;
    }

    private static float[] DownmixStereoToMono(float[] interleaved)
    {
        int n = interleaved.Length / 2;
        float[] result = new float[n];
        for (int i = 0; i < n; i++)
        {
            result[i] = 0.5f * (interleaved[2 * i] + interleaved[2 * i + 1]);
        }
        return result;
    }

    private static float[] DownmixToMono(float[] interleaved, int channels)
    {
        int n = interleaved.Length / channels;
        float[] result = new float[n];
        float invChannels = 1f / channels;
        for (int i = 0; i < n; i++)
        {
            float sum = 0f;
            int baseIdx = i * channels;
            for (int c = 0; c < channels; c++) sum += interleaved[baseIdx + c];
            result[i] = sum * invChannels;
        }
        return result;
    }

    /// <summary>
    /// Linear-interpolation resampler. Quality is fine for STT on speech;
    /// ↑ a sinc / polyphase resampler would be preferable for music or
    /// telephone-bandwidth-up-to-broadband upsampling, but speech is
    /// forgiving and the simpler code is right-sized for the demo path.
    /// </summary>
    private static float[] Resample(float[] input, int srcRate, int dstRate)
    {
        if (input.Length == 0) return input;
        if (srcRate == dstRate) return input;

        long outLen = (long)input.Length * dstRate / srcRate;
        float[] result = new float[outLen];
        double step = (double)srcRate / dstRate;
        double srcPos = 0;

        for (int i = 0; i < result.Length; i++)
        {
            int srcIdx = (int)srcPos;
            double frac = srcPos - srcIdx;

            if (srcIdx + 1 < input.Length)
            {
                result[i] = (float)(input[srcIdx] * (1 - frac) + input[srcIdx + 1] * frac);
            }
            else
            {
                // Edge: just copy the last sample.
                result[i] = input[srcIdx < input.Length ? srcIdx : input.Length - 1];
            }
            srcPos += step;
        }
        return result;
    }

    private static int ReadInt32LE(ReadOnlySpan<byte> buf, int offset)
        => buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24);

    private static short ReadInt16LE(ReadOnlySpan<byte> buf, int offset)
        => (short)(buf[offset] | (buf[offset + 1] << 8));
}
