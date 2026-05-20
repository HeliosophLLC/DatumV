using System.Diagnostics.CodeAnalysis;

namespace Heliosoph.DatumV.Serialization.Fits;

/// <summary>
/// Format handler for FITS (Flexible Image Transport System) files —
/// the standard astronomical data format. Matches <c>.fits</c>,
/// <c>.fit</c>, and <c>.fts</c> extensions (each transparently
/// composable with <c>.gz</c> through the descriptor's logical-extension
/// view) and the canonical <c>"SIMPLE&#160;&#160;=&#160;"</c> magic-byte
/// signature at offset 0 of an uncompressed file.
/// </summary>
public sealed class FitsFileFormat : IFileFormat
{
    /// <inheritdoc />
    public string Name => "fits";

    private static readonly string[] FitsExtensions = [".fits", ".fit", ".fts"];

    /// <summary>
    /// Magic-byte prefix every FITS primary header opens with. The first
    /// card is <c>SIMPLE  =                    T</c>; checking the first
    /// 10 bytes is enough to be unambiguous without consuming the full
    /// 80-byte card.
    /// </summary>
    private static readonly byte[] MagicPrefix = "SIMPLE  = "u8.ToArray();

    /// <inheritdoc />
    public bool CanHandle(
        FileFormatDescriptor descriptor,
        [NotNullWhen(true)] out IFormatDeserializer? deserializer)
    {
        string ext = descriptor.LogicalExtension;
        foreach (string candidate in FitsExtensions)
        {
            if (ext.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                deserializer = new FitsDeserializer(descriptor);
                return true;
            }
        }

        // Magic-byte detection. Only sensible for uncompressed inputs —
        // peeking a .gz source would see gzip magic, not the inner format's.
        // Gzipped inputs match by extension above.
        if (descriptor.Compression == CompressionKind.None
            && File.Exists(descriptor.FilePath)
            && HasFitsMagic(descriptor.FilePath))
        {
            deserializer = new FitsDeserializer(descriptor);
            return true;
        }

        deserializer = null;
        return false;
    }

    private static bool HasFitsMagic(string filePath)
    {
        Span<byte> buffer = stackalloc byte[10];
        using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        int read = stream.Read(buffer);
        if (read < MagicPrefix.Length) return false;
        for (int i = 0; i < MagicPrefix.Length; i++)
        {
            if (buffer[i] != MagicPrefix[i]) return false;
        }
        return true;
    }
}
