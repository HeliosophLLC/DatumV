namespace DatumIngest.Serialization;

/// <summary>
/// Describes an output target for format serialization. Provides the file path,
/// format-specific options, and a virtual <see cref="OpenAsync"/> method that
/// returns a writable <see cref="Stream"/>.
/// </summary>
/// <remarks>
/// Subclass and override <see cref="OpenAsync"/> to provide custom stream targets
/// (e.g. in-memory streams for testing, compression wrappers, cloud storage).
/// </remarks>
public class OutputDescriptor
{
    /// <summary>
    /// Creates a new descriptor for the given output file path.
    /// </summary>
    /// <param name="filePath">Absolute or relative path to the output file.</param>
    /// <param name="options">Optional format-specific key-value options.</param>
    public OutputDescriptor(string filePath, IReadOnlyDictionary<string, string>? options = null)
    {
        FilePath = filePath;
        Options = options ?? new Dictionary<string, string>();
    }

    /// <summary>The output file path, used for extension-based format detection and display.</summary>
    public string FilePath { get; }

    /// <summary>Format-specific options (e.g. "delimiter", "header").</summary>
    public IReadOnlyDictionary<string, string> Options { get; }

    /// <summary>
    /// Opens a writable stream for the output file.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A writable stream.</returns>
    public virtual Task<Stream> OpenAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<Stream>(
            new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536));
    }
}
