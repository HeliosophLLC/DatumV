using System.Diagnostics.CodeAnalysis;

namespace DatumIngest.Serialization.Idx;

/// <summary>
/// Format handler for MNIST-style IDX binary files. Matches <c>.idx</c> extension,
/// MNIST-style filename patterns (e.g. <c>train-images-idx3-ubyte</c>), and the
/// IDX magic byte signature (first two bytes <c>0x00 0x00</c>, third byte is a valid
/// type code, fourth byte is dimension count &gt;= 1).
/// </summary>
public sealed class IdxFileFormat : IFileFormat
{
    /// <inheritdoc />
    public string Name => "idx";

    /// <inheritdoc />
    public bool CanHandle(
        FileFormatDescriptor descriptor,
        [NotNullWhen(true)] out IFormatDeserializer? deserializer)
    {
        string ext = Path.GetExtension(descriptor.FilePath);
        if (ext.Equals(".idx", StringComparison.OrdinalIgnoreCase))
        {
            deserializer = new IdxDeserializer(descriptor);
            return true;
        }

        // MNIST-style filenames: train-images-idx3-ubyte, t10k-labels.idx1-ubyte, etc.
        string fileName = Path.GetFileName(descriptor.FilePath);
        if (IsIdxFilenamePattern(fileName))
        {
            deserializer = new IdxDeserializer(descriptor);
            return true;
        }

        // Magic byte detection.
        if (File.Exists(descriptor.FilePath) && HasIdxMagic(descriptor.FilePath))
        {
            deserializer = new IdxDeserializer(descriptor);
            return true;
        }

        deserializer = null;
        return false;
    }

    private static bool IsIdxFilenamePattern(string fileName)
    {
        return fileName.Contains("idx1-ubyte", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("idx2-ubyte", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("idx3-ubyte", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("idx4-ubyte", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasIdxMagic(string filePath)
    {
        Span<byte> buffer = stackalloc byte[4];
        using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        int read = stream.Read(buffer);
        return read == 4
            && buffer[0] == 0x00
            && buffer[1] == 0x00
            && IsValidTypeCode(buffer[2])
            && buffer[3] >= 1;
    }

    private static bool IsValidTypeCode(byte code)
    {
        return code is 0x08 or 0x09 or 0x0B or 0x0C or 0x0D or 0x0E;
    }
}
