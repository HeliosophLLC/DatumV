using System.Diagnostics.CodeAnalysis;

namespace Heliosoph.DatumV.Serialization.Parquet;

/// <summary>
/// Format handler for Apache Parquet files — the dominant on-disk
/// format for HuggingFace datasets, Spark output, dlt pipelines, and
/// pandas <c>.to_parquet()</c> exports. Matches the <c>.parquet</c>
/// extension plus the standard <c>PAR1</c> magic-byte signature
/// present at both the head and the footer of every conforming file.
/// </summary>
public sealed class ParquetFileFormat : IFileFormat
{
    /// <inheritdoc />
    public string Name => "parquet";

    private static readonly string[] ParquetExtensions = [".parquet", ".pq"];

    /// <summary>
    /// The 4-byte <c>PAR1</c> signature Apache Parquet writes at both
    /// the start of the file (data) and at the very end (footer).
    /// Either match is sufficient for detection; checking head-only
    /// keeps the I/O small.
    /// </summary>
    private static readonly byte[] MagicPrefix = "PAR1"u8.ToArray();

    /// <inheritdoc />
    public bool CanHandle(
        FileFormatDescriptor descriptor,
        [NotNullWhen(true)] out IFormatDeserializer? deserializer)
    {
        string ext = descriptor.LogicalExtension;
        foreach (string candidate in ParquetExtensions)
        {
            if (ext.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                deserializer = new ParquetDeserializer(descriptor);
                return true;
            }
        }

        if (descriptor.Compression == CompressionKind.None
            && File.Exists(descriptor.FilePath)
            && HasParquetMagic(descriptor.FilePath))
        {
            deserializer = new ParquetDeserializer(descriptor);
            return true;
        }

        deserializer = null;
        return false;
    }

    private static bool HasParquetMagic(string filePath)
    {
        Span<byte> buffer = stackalloc byte[4];
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
