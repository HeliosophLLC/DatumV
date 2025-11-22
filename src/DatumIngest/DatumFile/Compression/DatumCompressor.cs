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
    /// Compresses <paramref name="source"/> using the specified codec, returning a pooled
    /// buffer from <see cref="ArrayPool{T}.Shared"/> along with the number of compressed
    /// bytes written into it. The caller <em>must</em> return the buffer to the pool via
    /// <see cref="ArrayPool{T}.Return"/> after the bytes have been consumed.
    /// </summary>
    /// <param name="source">Raw bytes to compress.</param>
    /// <param name="kind">Codec to apply.</param>
    /// <param name="zstdLevel">Zstd compression level (1–22). Only used when <paramref name="kind"/> is Zstd.</param>
    /// <returns>
    /// A tuple of <c>(Buffer, Length)</c>: <c>Buffer</c> is a rented array whose first
    /// <c>Length</c> bytes contain the compressed output. Bytes beyond <c>Length</c> are
    /// unspecified.
    /// </returns>
    public static (byte[] Buffer, int Length) Compress(ReadOnlySpan<byte> source, DatumCompression kind, int zstdLevel = DatumFileConstants.DefaultZstdCompressionLevel)
    {
        return kind switch
        {
            DatumCompression.None => RentAndCopy(source),
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

    /// <summary>
    /// Decompresses <paramref name="source"/> into <paramref name="destination"/> without
    /// allocating an output array. Returns the number of bytes written.
    /// </summary>
    /// <param name="source">Compressed bytes.</param>
    /// <param name="destination">
    /// Caller-owned buffer that receives the decompressed bytes. Must be at least
    /// <paramref name="uncompressedLength"/> bytes long; excess capacity is ignored.
    /// </param>
    /// <param name="uncompressedLength">Expected decompressed byte count.</param>
    /// <param name="kind">Codec that was used to compress the bytes.</param>
    public static int DecompressInto(ReadOnlySpan<byte> source, byte[] destination, int uncompressedLength, DatumCompression kind)
    {
        return kind switch
        {
            DatumCompression.None => CopySourceInto(source, destination),
            DatumCompression.Zstd => DecompressZstdInto(source, destination),
            DatumCompression.Zlib => DecompressZlibInto(source, destination, uncompressedLength),
            DatumCompression.Brotli => DecompressBrotliInto(source, destination, uncompressedLength),
            _ => throw new NotSupportedException($"Unsupported compression kind: {kind}."),
        };
    }

    // ──────────────────── Zstd ────────────────────

    private static (byte[] Buffer, int Length) CompressZstd(ReadOnlySpan<byte> source, int level)
    {
        Compressor compressor = (_threadCompressor ??= new Compressor(level));
        compressor.Level = level;

        int bound = Compressor.GetCompressBound(source.Length);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bound);
        int compressedLength = compressor.Wrap(source, buffer);
        return (buffer, compressedLength);
    }

    /// <summary>
    /// Compresses <paramref name="source"/> using Zstd into <paramref name="destination"/>
    /// at the specified <paramref name="offset"/>. Returns the number of compressed bytes written.
    /// </summary>
    internal static int CompressZstdInto(
        ReadOnlySpan<byte> source,
        byte[] destination,
        int offset,
        int level = DatumFileConstants.DefaultZstdCompressionLevel)
    {
        Compressor compressor = (_threadCompressor ??= new Compressor(level));
        compressor.Level = level;
        return compressor.Wrap(source, destination.AsSpan(offset));
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

    private static int DecompressZstdInto(ReadOnlySpan<byte> source, byte[] destination)
    {
        Decompressor decompressor = (_threadDecompressor ??= new Decompressor());
        return decompressor.Unwrap(source, destination);
    }

    // ──────────────────── Zlib (Deflate) ────────────────────

    private static (byte[] Buffer, int Length) CompressZlib(ReadOnlySpan<byte> source)
    {
        using MemoryStream outputStream = new();
        using (DeflateStream deflate = new(outputStream, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(source);
        }

        return RentAndCopy(outputStream.GetBuffer().AsSpan(0, (int)outputStream.Length));
    }

    private static byte[] DecompressZlib(ReadOnlySpan<byte> source, int uncompressedLength)
    {
        using MemoryStream inputStream = new(source.ToArray());
        using DeflateStream deflate = new(inputStream, CompressionMode.Decompress, leaveOpen: true);
        byte[] output = new byte[uncompressedLength];
        deflate.ReadExactly(output);
        return output;
    }

    private static int DecompressZlibInto(ReadOnlySpan<byte> source, byte[] destination, int uncompressedLength)
    {
        using MemoryStream inputStream = new(source.ToArray());
        using DeflateStream deflate = new(inputStream, CompressionMode.Decompress, leaveOpen: true);
        deflate.ReadExactly(destination.AsSpan(0, uncompressedLength));
        return uncompressedLength;
    }

    // ──────────────────── Brotli ────────────────────

    private static (byte[] Buffer, int Length) CompressBrotli(ReadOnlySpan<byte> source)
    {
        using MemoryStream outputStream = new();
        using (BrotliStream brotli = new(outputStream, CompressionLevel.Optimal, leaveOpen: true))
        {
            brotli.Write(source);
        }

        return RentAndCopy(outputStream.GetBuffer().AsSpan(0, (int)outputStream.Length));
    }

    private static byte[] DecompressBrotli(ReadOnlySpan<byte> source, int uncompressedLength)
    {
        using MemoryStream inputStream = new(source.ToArray());
        using BrotliStream brotli = new(inputStream, CompressionMode.Decompress, leaveOpen: true);
        byte[] output = new byte[uncompressedLength];
        brotli.ReadExactly(output);
        return output;
    }

    private static int DecompressBrotliInto(ReadOnlySpan<byte> source, byte[] destination, int uncompressedLength)
    {
        using MemoryStream inputStream = new(source.ToArray());
        using BrotliStream brotli = new(inputStream, CompressionMode.Decompress, leaveOpen: true);
        brotli.ReadExactly(destination.AsSpan(0, uncompressedLength));
        return uncompressedLength;
    }

    private static int CopySourceInto(ReadOnlySpan<byte> source, byte[] destination)
    {
        source.CopyTo(destination);
        return source.Length;
    }

    /// <summary>
    /// Rents a pooled buffer sized to <paramref name="source"/> and copies the bytes into it.
    /// Used by the <see cref="DatumCompression.None"/> path and by the Zlib/Brotli paths that
    /// go through a <see cref="MemoryStream"/> before surfacing to the pool-aware API.
    /// </summary>
    private static (byte[] Buffer, int Length) RentAndCopy(ReadOnlySpan<byte> source)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(source.Length);
        source.CopyTo(buffer);
        return (buffer, source.Length);
    }
}
