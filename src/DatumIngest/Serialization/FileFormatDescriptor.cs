using System.Buffers;
using System.IO.Compression;
using DatumIngest.Catalog;
using ICSharpCode.SharpZipLib.BZip2;

namespace DatumIngest.Serialization;

/// <summary>
/// Describes a source file for format deserialization. Provides the file path,
/// format-specific options, and a virtual <see cref="OpenAsync"/> method that
/// returns a seekable <see cref="Stream"/> for reading.
/// </summary>
/// <remarks>
/// <para>
/// When the file path ends in <c>.gz</c> or <c>.bz2</c> the descriptor treats it
/// as compressed and transparently materializes a decompressed temp file on first
/// <see cref="OpenAsync"/>. Subsequent opens reuse the same temp file, so formats
/// that need seekable streams (ZIP, Parquet, HDF5) or perform multi-pass reads
/// (CSV scan + ingest) both Just Work. Disposing the descriptor deletes the temp
/// file.
/// </para>
/// <para>
/// Decompressed size is capped at <see cref="DefaultMaxDecompressedBytes"/> unless a
/// larger limit is passed to the constructor. The cap exists to protect multi-tenant
/// hosts from a rogue upload filling the disk — gzip and bzip2 routinely expand 3–10×
/// (and bzip2 occasionally far more) so a modestly sized archive can produce a very
/// large decompressed payload.
/// </para>
/// </remarks>
public class FileFormatDescriptor : IDisposable
{
    /// <summary>
    /// Default safety cap on decompressed size for gzipped inputs. Set to 10 GiB.
    /// Override via the constructor for sources known to be larger.
    /// </summary>
    public const long DefaultMaxDecompressedBytes = 10L * 1024 * 1024 * 1024;

    private readonly long _maxDecompressedBytes;
    private string? _materializedPath;
    private bool _disposed;

    /// <summary>
    /// Creates a new descriptor for the given file path.
    /// </summary>
    /// <param name="filePath">Absolute or relative path to the source file.</param>
    /// <param name="options">Optional format-specific key-value options (e.g. delimiter, header).</param>
    /// <param name="maxDecompressedBytes">
    /// Maximum bytes permitted from decompressing a gzipped / bzipped source.
    /// Exceeding this aborts ingestion and deletes the partial temp file.
    /// Defaults to <see cref="DefaultMaxDecompressedBytes"/>.
    /// </param>
    public FileFormatDescriptor(
        string filePath,
        IReadOnlyDictionary<string, string>? options = null,
        long maxDecompressedBytes = DefaultMaxDecompressedBytes)
    {
        FilePath = filePath;
        Options = options ?? new Dictionary<string, string>();
        _maxDecompressedBytes = maxDecompressedBytes;
        Compression = DetectCompression(filePath);
    }

    /// <summary>The file path, used for extension-based format detection and display.</summary>
    public string FilePath { get; }

    /// <summary>Format-specific options (e.g. "delimiter", "header").</summary>
    public IReadOnlyDictionary<string, string> Options { get; }

    /// <summary>
    /// Compression applied to the source file. Detected from the path extension.
    /// </summary>
    public CompressionKind Compression { get; }

    /// <summary>
    /// The data-format extension after stripping any compression wrapper.
    /// For <c>foo.csv.gz</c> returns <c>.csv</c>; for <c>foo.csv</c> returns <c>.csv</c>.
    /// Use this instead of <see cref="Path.GetExtension(string)"/> in <c>CanHandle</c>
    /// implementations so gzipped inputs are recognised without special-casing.
    /// </summary>
    public string LogicalExtension
    {
        get
        {
            if (Compression == CompressionKind.None)
            {
                return Path.GetExtension(FilePath);
            }

            string compressionExt = Path.GetExtension(FilePath);
            string innerPath = FilePath[..^compressionExt.Length];
            return Path.GetExtension(innerPath);
        }
    }

    /// <summary>
    /// The filename after stripping any compression wrapper.
    /// For <c>train-images-idx3-ubyte.gz</c> returns <c>train-images-idx3-ubyte</c>.
    /// Use this for filename-pattern based detection so compressed inputs match the
    /// same patterns as uncompressed ones.
    /// </summary>
    public string LogicalFileName
    {
        get
        {
            string name = Path.GetFileName(FilePath);
            if (Compression == CompressionKind.None)
            {
                return name;
            }

            string compressionExt = Path.GetExtension(FilePath);
            return name[..^compressionExt.Length];
        }
    }

    /// <summary>
    /// The path that actually holds readable data. Equals <see cref="FilePath"/> for
    /// uncompressed sources; equals the temp-file path once a gzipped source has been
    /// materialized. Callers performing magic-byte detection should prefer
    /// <see cref="OpenAsync"/> — opening <see cref="EffectivePath"/> before the first
    /// <see cref="OpenAsync"/> call returns the still-compressed original.
    /// </summary>
    public string EffectivePath => _materializedPath ?? FilePath;

    /// <summary>
    /// Opens a readable stream for the source file. For gzipped inputs, the first call
    /// materializes a decompressed temp file; subsequent calls return a fresh stream
    /// over that temp file (so the result is always seekable and supports multi-pass
    /// readers like the CSV scanner).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A seekable stream positioned at the beginning of the data.</returns>
    /// <exception cref="InvalidDataException">
    /// Thrown when the decompressed size of a gzipped / bzipped source exceeds
    /// <see cref="DefaultMaxDecompressedBytes"/> (or the caller-provided override).
    /// </exception>
    public virtual async Task<Stream> OpenAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(FileFormatDescriptor));
        }

        if (Compression != CompressionKind.None && _materializedPath is null)
        {
            _materializedPath = await MaterializeCompressedAsync(cancellationToken).ConfigureAwait(false);
        }

        return new FileStream(
            EffectivePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_materializedPath is not null)
        {
            TryDelete(_materializedPath);
            _materializedPath = null;
        }
    }

    private async Task<string> MaterializeCompressedAsync(CancellationToken cancellationToken)
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"datumingest-{Guid.NewGuid():N}.tmp");

        try
        {
            await using FileStream source = new(
                FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
            await using Stream decompressed = OpenDecompressionStream(source, Compression);
            await using FileStream dest = new(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);

            byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                long totalBytes = 0;
                int read;
                while ((read = await decompressed.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false)) > 0)
                {
                    totalBytes += read;
                    if (totalBytes > _maxDecompressedBytes)
                    {
                        throw new InvalidDataException(
                            $"Decompressed size of '{FilePath}' exceeds the configured limit of " +
                            $"{_maxDecompressedBytes:N0} bytes. Raise the limit via the " +
                            $"FileFormatDescriptor maxDecompressedBytes parameter or decompress " +
                            $"the source manually before ingestion.");
                    }

                    await dest.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return tempPath;
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static Stream OpenDecompressionStream(Stream source, CompressionKind kind) => kind switch
    {
        CompressionKind.Gzip => new GZipStream(source, CompressionMode.Decompress),
        // BZip2InputStream is synchronous-only — that's fine, the surrounding
        // MaterializeCompressedAsync pumps it through async file IO on the way out.
        CompressionKind.Bzip2 => new BZip2InputStream(source),
        _ => throw new InvalidOperationException(
            $"OpenDecompressionStream called with non-compressed kind '{kind}'."),
    };

    private static CompressionKind DetectCompression(string filePath)
    {
        string ext = Path.GetExtension(filePath);
        if (ext.Equals(".gz", StringComparison.OrdinalIgnoreCase))
        {
            return CompressionKind.Gzip;
        }
        if (ext.Equals(".bz2", StringComparison.OrdinalIgnoreCase))
        {
            return CompressionKind.Bzip2;
        }
        return CompressionKind.None;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best-effort cleanup */ }
    }
}
