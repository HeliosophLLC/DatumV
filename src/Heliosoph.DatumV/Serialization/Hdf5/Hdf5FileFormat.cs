using System.Diagnostics.CodeAnalysis;

namespace Heliosoph.DatumV.Serialization.Hdf5;

/// <summary>
/// Format handler for HDF5 (Hierarchical Data Format v5) files — the
/// de facto storage for ML datasets, scientific arrays, and Python
/// pickled tensors. Matches <c>.h5</c>, <c>.hdf5</c>, and <c>.hdf</c>
/// extensions, plus the 8-byte HDF5 magic signature
/// <c>\x89HDF\r\n\x1a\n</c> at the head of an uncompressed file.
/// </summary>
public sealed class Hdf5FileFormat : IFileFormat
{
    /// <inheritdoc />
    public string Name => "hdf5";

    private static readonly string[] Hdf5Extensions = [".h5", ".hdf5", ".hdf"];

    /// <summary>
    /// 8-byte HDF5 signature at file offset 0 (the format spec calls
    /// this the "user block magic" — present on every conforming file
    /// regardless of internal layout).
    /// </summary>
    private static readonly byte[] MagicSignature =
        [0x89, (byte)'H', (byte)'D', (byte)'F', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <inheritdoc />
    public bool CanHandle(
        FileFormatDescriptor descriptor,
        [NotNullWhen(true)] out IFormatDeserializer? deserializer)
    {
        string ext = descriptor.LogicalExtension;
        foreach (string candidate in Hdf5Extensions)
        {
            if (ext.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                deserializer = new Hdf5Deserializer(descriptor);
                return true;
            }
        }

        if (descriptor.Compression == CompressionKind.None
            && File.Exists(descriptor.FilePath)
            && HasHdf5Magic(descriptor.FilePath))
        {
            deserializer = new Hdf5Deserializer(descriptor);
            return true;
        }

        deserializer = null;
        return false;
    }

    private static bool HasHdf5Magic(string filePath)
    {
        Span<byte> buffer = stackalloc byte[8];
        using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        int read = stream.Read(buffer);
        if (read < MagicSignature.Length) return false;
        for (int i = 0; i < MagicSignature.Length; i++)
        {
            if (buffer[i] != MagicSignature[i]) return false;
        }
        return true;
    }
}
