using System.Text;

namespace DatumIngest.Serialization;

/// <summary>
/// An <see cref="OutputDescriptor"/> backed by an in-memory stream.
/// Useful for testing serializers without writing to disk.
/// </summary>
public sealed class MemoryOutputDescriptor : OutputDescriptor
{
    private readonly MemoryStream _stream = new();

    /// <summary>Creates a memory-backed output descriptor.</summary>
    /// <param name="fileName">Logical file name (for extension-based detection).</param>
    /// <param name="options">Optional format-specific options.</param>
    public MemoryOutputDescriptor(string fileName = "output", IReadOnlyDictionary<string, string>? options = null)
        : base(fileName, options)
    {
    }

    /// <inheritdoc/>
    public override Task<Stream> OpenAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<Stream>(_stream);

    /// <summary>Returns the written content as a UTF-8 string.</summary>
    public string GetOutput() => Encoding.UTF8.GetString(_stream.ToArray());

    /// <summary>Returns the written content as a byte array.</summary>
    public byte[] GetBytes() => _stream.ToArray();
}
