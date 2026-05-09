using System.Diagnostics.CodeAnalysis;
using DatumIngest.Catalog;
using DatumIngest.Serialization.MediaBag;

namespace DatumIngest.Serialization.Zip;

/// <summary>
/// Format handler for ZIP archive files. Matches <c>.zip</c> extension and the
/// ZIP local file header magic <c>PK\x03\x04</c>. The actual per-entry
/// deserialization is delegated to <see cref="MediaBagDeserializer"/> via a
/// <see cref="ZipBagReader"/>; the schema is decided by the homogeneous-media
/// probe rather than baked into this format handler.
/// </summary>
public sealed class ZipFileFormat : IFileFormat
{
    /// <inheritdoc />
    public string Name => "zip";

    /// <inheritdoc />
    public bool CanHandle(
        FileFormatDescriptor descriptor,
        [NotNullWhen(true)] out IFormatDeserializer? deserializer)
    {
        string ext = descriptor.LogicalExtension;
        if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            deserializer = new MediaBagDeserializer(new ZipBagReader(descriptor));
            return true;
        }

        // Magic byte detection — only sensible for uncompressed inputs. Gzipping
        // a ZIP is nonsense anyway (ZIP has its own internal compression).
        if (descriptor.Compression == CompressionKind.None
            && File.Exists(descriptor.FilePath)
            && HasZipMagic(descriptor.FilePath))
        {
            deserializer = new MediaBagDeserializer(new ZipBagReader(descriptor));
            return true;
        }

        deserializer = null;
        return false;
    }

    private static bool HasZipMagic(string filePath)
    {
        Span<byte> buffer = stackalloc byte[4];
        using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        int read = stream.Read(buffer);
        return read == 4 && buffer[0] == 'P' && buffer[1] == 'K' && buffer[2] == 0x03 && buffer[3] == 0x04;
    }
}
