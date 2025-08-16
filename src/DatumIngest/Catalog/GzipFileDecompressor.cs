using System.IO.Compression;

namespace DatumIngest.Catalog;

/// <summary>
/// Decompresses a gzip file to a temporary file on disk. The temporary file is
/// deleted when this instance is disposed, providing deterministic cleanup for
/// providers that require seekable file access (Parquet, HDF5, ZIP, IDX, DatumFile).
/// </summary>
public sealed class GzipFileDecompressor : IDisposable
{
    /// <summary>
    /// Path to the decompressed temporary file.
    /// </summary>
    public string DecompressedFilePath { get; }

    private bool _disposed;

    private GzipFileDecompressor(string decompressedFilePath)
    {
        DecompressedFilePath = decompressedFilePath;
    }

    /// <summary>
    /// Decompresses the gzip file at <paramref name="gzipFilePath"/> to a temporary
    /// file and returns a <see cref="GzipFileDecompressor"/> that tracks the temp file
    /// for cleanup.
    /// </summary>
    /// <param name="gzipFilePath">Path to the gzip-compressed source file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A <see cref="GzipFileDecompressor"/> whose <see cref="DecompressedFilePath"/>
    /// points to the decompressed temporary file.
    /// </returns>
    public static async Task<GzipFileDecompressor> DecompressAsync(
        string gzipFilePath,
        CancellationToken cancellationToken)
    {
        string tempFilePath = Path.Combine(
            Path.GetTempPath(),
            $"datum_gz_{Guid.NewGuid():N}");

        try
        {
            await using FileStream source = new(
                gzipFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, useAsync: true);

            await using GZipStream gzipStream = new(source, CompressionMode.Decompress);

            await using FileStream target = new(
                tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true);

            await gzipStream.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Clean up the partial temp file on failure.
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }

            throw;
        }

        return new GzipFileDecompressor(tempFilePath);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (File.Exists(DecompressedFilePath))
            {
                File.Delete(DecompressedFilePath);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup — the OS will eventually clean the temp directory.
        }
    }
}
