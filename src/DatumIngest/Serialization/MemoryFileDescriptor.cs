using System.Text;

namespace DatumIngest.Serialization;

/// <summary>
/// A <see cref="FileFormatDescriptor"/> backed by in-memory string content.
/// Useful for testing deserializers without reading from disk.
/// </summary>
public sealed class MemoryFileDescriptor : FileFormatDescriptor
{
    private readonly string _content;

    /// <summary>Creates a memory-backed file descriptor.</summary>
    /// <param name="content">The file content as a string.</param>
    /// <param name="fileName">Logical file name (for extension-based detection).</param>
    /// <param name="options">Optional format-specific options.</param>
    public MemoryFileDescriptor(string content, string fileName = "input",
        IReadOnlyDictionary<string, string>? options = null)
        : base(fileName, options)
    {
        _content = content;
    }

    /// <inheritdoc/>
    public override Task<Stream> OpenAsync(CancellationToken cancellationToken = default)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(_content);
        return Task.FromResult<Stream>(new MemoryStream(bytes));
    }
}
