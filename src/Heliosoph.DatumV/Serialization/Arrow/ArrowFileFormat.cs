using System.Diagnostics.CodeAnalysis;

namespace Heliosoph.DatumV.Serialization.Arrow;

/// <summary>
/// Format handler for Apache Arrow IPC / Feather v2 files — the native
/// on-disk format for the HuggingFace <c>datasets</c> library, Polars
/// cache writes, DuckDB exports, and pandas <c>.to_feather()</c>.
/// Matches <c>.arrow</c> and <c>.feather</c> extensions plus the
/// standard <c>ARROW1\0\0</c> magic-byte signature at the head of every
/// conforming file (file format, not stream format).
/// </summary>
public sealed class ArrowFileFormat : IFileFormat
{
    /// <inheritdoc />
    public string Name => "arrow";

    private static readonly string[] ArrowExtensions = [".arrow", ".feather"];

    /// <summary>
    /// The 8-byte <c>ARROW1\0\0</c> signature Apache Arrow writes at the
    /// start of every file-format IPC stream (Feather v2 included). Note
    /// the stream-format variant uses a different framing and isn't
    /// matched here — open_arrow_meta / open_arrow target file format.
    /// </summary>
    private static readonly byte[] MagicPrefix = "ARROW1\0\0"u8.ToArray();

    /// <inheritdoc />
    public bool CanHandle(
        FileFormatDescriptor descriptor,
        [NotNullWhen(true)] out IFormatDeserializer? deserializer)
    {
        string ext = descriptor.LogicalExtension;
        foreach (string candidate in ArrowExtensions)
        {
            if (ext.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                deserializer = new ArrowDeserializer(descriptor);
                return true;
            }
        }

        if (descriptor.Compression == CompressionKind.None
            && File.Exists(descriptor.FilePath)
            && HasArrowMagic(descriptor.FilePath))
        {
            deserializer = new ArrowDeserializer(descriptor);
            return true;
        }

        deserializer = null;
        return false;
    }

    private static bool HasArrowMagic(string filePath)
    {
        Span<byte> buffer = stackalloc byte[8];
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
