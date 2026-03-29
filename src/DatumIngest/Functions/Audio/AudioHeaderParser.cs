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
/// <para>Supports WAV today (RIFF/WAVE header). MP3, FLAC, OGG, and others return
/// <c>null</c> — extend with per-format readers as needed.</para>
/// <para>The parser is intentionally permissive: returns <c>null</c> for any
/// malformed input rather than throwing. Callers fall back to the no-metadata
/// factory and accessors return zero sentinels.</para>
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
}

/// <summary>
/// Audio metadata extracted from a container header. Channels and bit depth are
/// clamped to <see cref="byte"/> (255 max — covers every real-world value).
/// </summary>
public sealed record AudioMetadata(uint SampleRate, byte Channels, byte BitDepth, uint FrameCount);
