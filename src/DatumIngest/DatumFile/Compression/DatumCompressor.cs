using System.Buffers;
using System.IO.Compression;
using ZstdSharp;

namespace DatumIngest.DatumFile.Compression;

/// <summary>
/// Compresses and decompresses column page payloads using the codec identified
/// by <see cref="DatumCompression"/>. Delegates to ZstdSharp.Port for Zstd,
/// and to BCL streams for Zlib and Brotli.
/// </summary>
public static class DatumCompressor
{
    /// <summary>
    /// Thread-local reusable Zstd compressor context. Avoids allocating a ~140 KiB native
    /// ZSTD_CCtx (with a <see cref="System.Runtime.ConstrainedExecution.CriticalFinalizerObject"/>)
    /// on every <see cref="CompressZstd"/> call.
    /// </summary>
    [ThreadStatic]
    private static Compressor? _threadCompressor;

    /// <summary>
    /// Thread-local reusable Zstd decompressor context.
    /// </summary>
    [ThreadStatic]
    private static Decompressor? _threadDecompressor;
    /// <summary>
    /// Compresses <paramref name="source"/> using the specified codec.
    /// Returns the unmodified source bytes when <paramref name="kind"/> is <see cref="DatumCompression.None"/>.
    /// </summary>
    /// <param name="source">Raw bytes to compress.</param>
    /// <param name="kind">Codec to apply.</param>
    /// <param name="zstdLevel">Zstd compression level (1–22). Only used when <paramref name="kind"/> is Zstd.</param>
    public static byte[] Compress(ReadOnlySpan<byte> source, DatumCompression kind, int zstdLevel = DatumFileConstants.DefaultZstdCompressionLevel)
    {
        return kind switch
        {
            DatumCompression.None => source.ToArray(),
            DatumCompression.Zstd => CompressZstd(source, zstdLevel),
            DatumCompression.Zlib => CompressZlib(source),
            DatumCompression.Brotli => CompressBrotli(source),
            _ => throw new NotSupportedException($"Unsupported compression kind: {kind}."),
        };
    }

    /// <summary>
    /// Decompresses <paramref name="source"/> into a fresh byte array of
    /// <paramref name="uncompressedLength"/> bytes.
    /// Returns a copy of the source when <paramref name="kind"/> is <see cref="DatumCompression.None"/>.
    /// </summary>
    /// <param name="source">Compressed bytes.</param>
    /// <param name="uncompressedLength">Expected decompressed byte count. Used to pre-allocate the output buffer.</param>
    /// <param name="kind">Codec that was used to compress the bytes.</param>
    public static byte[] Decompress(ReadOnlySpan<byte> source, int uncompressedLength, DatumCompression kind)
    {
        return kind switch
        {
            DatumCompression.None => source.ToArray(),
            DatumCompression.Zstd => DecompressZstd(source, uncompressedLength),
            DatumCompression.Zlib => DecompressZlib(source, uncompressedLength),
            DatumCompression.Brotli => DecompressBrotli(source, uncompressedLength),
            _ => throw new NotSupportedException($"Unsupported compression kind: {kind}."),
        };
    }

    // ──────────────────── Zstd ────────────────────

    private static byte[] CompressZstd(ReadOnlySpan<byte> source, int level)
    {
        Compressor compressor = (_threadCompressor ??= new Compressor(level));
        compressor.Level = level;

        int bound = Compressor.GetCompressBound(source.Length);
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(bound);

        try
        {
            int compressedLength = compressor.Wrap(source, rentedBuffer);
            byte[] result = new byte[compressedLength];
            Buffer.BlockCopy(rentedBuffer, 0, result, 0, compressedLength);
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
        }
    }

    private static byte[] DecompressZstd(ReadOnlySpan<byte> source, int uncompressedLength)
    {
        byte[] output = new byte[uncompressedLength];
        Decompressor decompressor = (_threadDecompressor ??= new Decompressor());
        int written = decompressor.Unwrap(source, output);

        if (written != uncompressedLength)
        {
            Array.Resize(ref output, written);
        }

        return output;
    }

    // ──────────────────── Zlib (Deflate) ────────────────────

    private static byte[] CompressZlib(ReadOnlySpan<byte> source)
    {
        using MemoryStream outputStream = new();
        using (DeflateStream deflate = new(outputStream, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(source);
        }

        return outputStream.ToArray();
    }

    private static byte[] DecompressZlib(ReadOnlySpan<byte> source, int uncompressedLength)
    {
        using MemoryStream inputStream = new(source.ToArray());
        using DeflateStream deflate = new(inputStream, CompressionMode.Decompress, leaveOpen: true);
        byte[] output = new byte[uncompressedLength];
        deflate.ReadExactly(output);
        return output;
    }

    // ──────────────────── Brotli ────────────────────

    private static byte[] CompressBrotli(ReadOnlySpan<byte> source)
    {
        using MemoryStream outputStream = new();
        using (BrotliStream brotli = new(outputStream, CompressionLevel.Optimal, leaveOpen: true))
        {
            brotli.Write(source);
        }

        return outputStream.ToArray();
    }

    private static byte[] DecompressBrotli(ReadOnlySpan<byte> source, int uncompressedLength)
    {
        using MemoryStream inputStream = new(source.ToArray());
        using BrotliStream brotli = new(inputStream, CompressionMode.Decompress, leaveOpen: true);
        byte[] output = new byte[uncompressedLength];
        brotli.ReadExactly(output);
        return output;
    }
}
