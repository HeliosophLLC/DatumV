using System.Text;

namespace DatumIngest.Serialization;

/// <summary>
/// A <see cref="FileFormatDescriptor"/> backed by in-memory content.
/// Useful for testing deserializers without reading from disk.
/// </summary>
public sealed class MemoryFileDescriptor : FileFormatDescriptor
{
    private readonly byte[] _bytes;

    /// <summary>Creates a memory-backed file descriptor from string content.</summary>
    /// <param name="content">The file content as a string (UTF-8 encoded).</param>
    /// <param name="fileName">Logical file name (for extension-based detection).</param>
    /// <param name="options">Optional format-specific options.</param>
    public MemoryFileDescriptor(string content, string fileName = "input",
        IReadOnlyDictionary<string, string>? options = null)
        : base(fileName, options)
    {
        _bytes = Encoding.UTF8.GetBytes(content);
    }

    /// <summary>Creates a memory-backed file descriptor from raw bytes.</summary>
    /// <param name="bytes">The raw file content.</param>
    /// <param name="fileName">Logical file name (for extension-based detection).</param>
    /// <param name="options">Optional format-specific options.</param>
    public MemoryFileDescriptor(byte[] bytes, string fileName = "input",
        IReadOnlyDictionary<string, string>? options = null)
        : base(fileName, options)
    {
        _bytes = bytes;
    }

    /// <inheritdoc/>
    public override Task<Stream> OpenAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<Stream>(new MemoryStream(_bytes));
    }
}
