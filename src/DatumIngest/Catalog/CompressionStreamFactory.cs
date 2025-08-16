using System.IO.Compression;

namespace DatumIngest.Catalog;

/// <summary>
/// Opens a data file for reading, automatically wrapping the stream in a
/// decompression layer when the descriptor indicates compression.
/// </summary>
public static class CompressionStreamFactory
{
    /// <summary>
    /// Opens the file referenced by <paramref name="descriptor"/> for reading.
    /// When <see cref="TableDescriptor.Compression"/> is <see cref="CompressionKind.Gzip"/>,
    /// the returned stream transparently decompresses gzip content.
    /// </summary>
    /// <param name="descriptor">Descriptor identifying the file and its compression.</param>
    /// <returns>A readable stream, optionally wrapped in a decompression layer.</returns>
    public static Stream OpenRead(TableDescriptor descriptor)
    {
        FileStream fileStream = File.OpenRead(descriptor.FilePath);

        return descriptor.Compression switch
        {
            CompressionKind.Gzip => new GZipStream(fileStream, CompressionMode.Decompress),
            _ => fileStream,
        };
    }

    /// <summary>
    /// Opens the file referenced by <paramref name="descriptor"/> as a
    /// <see cref="StreamReader"/>, automatically decompressing gzip content
    /// when indicated by the descriptor.
    /// </summary>
    /// <param name="descriptor">Descriptor identifying the file and its compression.</param>
    /// <returns>A text reader over the (possibly decompressed) file content.</returns>
    public static StreamReader OpenText(TableDescriptor descriptor)
    {
        return new StreamReader(OpenRead(descriptor));
    }
}
