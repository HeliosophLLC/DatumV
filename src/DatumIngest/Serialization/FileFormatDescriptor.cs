namespace DatumIngest.Serialization;

/// <summary>
/// Describes a source file for format deserialization. Provides the file path,
/// format-specific options, and a virtual <see cref="OpenAsync"/> method that
/// returns a seekable <see cref="Stream"/> for reading.
/// </summary>
/// <remarks>
/// Subclass and override <see cref="OpenAsync"/> to provide custom stream sources
/// (e.g. in-memory streams for testing, decompression wrappers, cloud storage).
/// </remarks>
public class FileFormatDescriptor
{
    /// <summary>
    /// Creates a new descriptor for the given file path.
    /// </summary>
    /// <param name="filePath">Absolute or relative path to the source file.</param>
    /// <param name="options">Optional format-specific key-value options (e.g. delimiter, header).</param>
    public FileFormatDescriptor(string filePath, IReadOnlyDictionary<string, string>? options = null)
    {
        FilePath = filePath;
        Options = options ?? new Dictionary<string, string>();
    }

    /// <summary>The file path, used for extension-based format detection and display.</summary>
    public string FilePath { get; }

    /// <summary>Format-specific options (e.g. "delimiter", "header").</summary>
    public IReadOnlyDictionary<string, string> Options { get; }

    /// <summary>
    /// Opens a readable, seekable stream for the source file.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A seekable stream positioned at the beginning of the file.</returns>
    public virtual Task<Stream> OpenAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<Stream>(
            new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536));
    }
}
