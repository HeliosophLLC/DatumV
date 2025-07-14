namespace DatumIngest.Catalog;

/// <summary>
/// Detects the provider name for a data file using file extension,
/// filename patterns, and magic byte signatures. Returns <c>null</c>
/// when the format cannot be determined.
/// </summary>
public static class FileFormatDetector
{
    /// <summary>
    /// Maximum number of bytes read from the file header for magic byte detection.
    /// </summary>
    private const int MagicByteBufferLength = 8;

    /// <summary>
    /// Maps well-known file extensions to provider names.
    /// </summary>
    private static readonly Dictionary<string, string> ExtensionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".csv"] = "csv",
        [".tsv"] = "csv",
        [".json"] = "json",
        [".jsonl"] = "jsonl",
        [".ndjson"] = "jsonl",
        [".parquet"] = "parquet",
        [".pq"] = "parquet",
        [".hdf5"] = "hdf5",
        [".h5"] = "hdf5",
        [".hdf"] = "hdf5",
        [".zip"] = "zip",
        [".idx"] = "idx",
    };

    /// <summary>
    /// Attempts to detect the provider name for the given file path.
    /// Uses a three-tier strategy: file extension, filename pattern
    /// (for MNIST-style IDX files), then magic byte signatures.
    /// </summary>
    /// <param name="filePath">Absolute or relative path to the data file.</param>
    /// <returns>
    /// The provider name (e.g. "csv", "parquet", "idx"), or <c>null</c>
    /// if the format cannot be determined.
    /// </returns>
    public static string? DetectProvider(string filePath)
    {
        // Tier 1: file extension.
        string extension = Path.GetExtension(filePath);
        if (extension.Length > 0 && ExtensionMap.TryGetValue(extension, out string? provider))
        {
            return provider;
        }

        // Tier 2: filename pattern — MNIST-style IDX filenames like "train-images-idx3-ubyte".
        string fileName = Path.GetFileName(filePath);
        if (IsIdxFilenamePattern(fileName))
        {
            return "idx";
        }

        // Tier 3: magic bytes (only if the file exists and is readable).
        if (File.Exists(filePath))
        {
            return DetectFromMagicBytes(filePath);
        }

        return null;
    }

    /// <summary>
    /// Checks whether the filename matches MNIST-style IDX naming conventions
    /// (e.g. "train-images-idx3-ubyte", "t10k-labels.idx1-ubyte").
    /// </summary>
    private static bool IsIdxFilenamePattern(string fileName)
    {
        return fileName.Contains("idx1-ubyte", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("idx2-ubyte", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("idx3-ubyte", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("idx4-ubyte", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Attempts to detect the provider name by reading magic byte signatures
    /// from the given stream. If the stream is seekable, the position is
    /// restored after reading.
    /// </summary>
    /// <param name="stream">A readable stream positioned at the start of the data.</param>
    /// <returns>
    /// The provider name (e.g. "parquet", "hdf5", "json"), or <c>null</c>
    /// if the format cannot be determined from magic bytes alone.
    /// </returns>
    public static string? DetectProvider(Stream stream)
    {
        long originalPosition = stream.CanSeek ? stream.Position : -1;

        Span<byte> buffer = stackalloc byte[MagicByteBufferLength];
        int bytesRead = stream.Read(buffer);

        if (stream.CanSeek)
        {
            stream.Position = originalPosition;
        }

        return MatchMagicBytes(buffer[..bytesRead]);
    }

    /// <summary>
    /// Reads the first few bytes of a file and attempts to identify its format
    /// via well-known magic byte signatures.
    /// </summary>
    private static string? DetectFromMagicBytes(string filePath)
    {
        Span<byte> buffer = stackalloc byte[MagicByteBufferLength];

        using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        int bytesRead = stream.Read(buffer);

        return MatchMagicBytes(buffer[..bytesRead]);
    }

    /// <summary>
    /// Matches a header buffer against well-known magic byte signatures.
    /// </summary>
    private static string? MatchMagicBytes(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4)
        {
            return null;
        }

        // Parquet: "PAR1" magic at start of file.
        if (header[0] == 'P' && header[1] == 'A' && header[2] == 'R' && header[3] == '1')
        {
            return "parquet";
        }

        // HDF5: 8-byte signature "\x89HDF\r\n\x1a\n".
        if (header.Length >= 8
            && header[0] == 0x89 && header[1] == 'H' && header[2] == 'D' && header[3] == 'F'
            && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
        {
            return "hdf5";
        }

        // ZIP: local file header "PK\x03\x04".
        if (header[0] == 'P' && header[1] == 'K' && header[2] == 0x03 && header[3] == 0x04)
        {
            return "zip";
        }

        // IDX: first two bytes 0x00, third byte is a valid type code, fourth is dimension count >= 1.
        if (header[0] == 0x00 && header[1] == 0x00 && IsValidIdxTypeCode(header[2]) && header[3] >= 1)
        {
            return "idx";
        }

        // JSON: first non-whitespace byte is '{' or '['.
        byte firstNonWhitespace = FindFirstNonWhitespace(header);
        if (firstNonWhitespace == '{' || firstNonWhitespace == '[')
        {
            return "json";
        }

        return null;
    }

    /// <summary>
    /// Returns whether the byte is a valid IDX data type code.
    /// </summary>
    private static bool IsValidIdxTypeCode(byte code)
    {
        return code is 0x08 or 0x09 or 0x0B or 0x0C or 0x0D or 0x0E;
    }

    /// <summary>
    /// Returns the first non-ASCII-whitespace byte in the span,
    /// or <c>0</c> if the span is empty or all whitespace.
    /// </summary>
    private static byte FindFirstNonWhitespace(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            if (b != ' ' && b != '\t' && b != '\r' && b != '\n')
            {
                return b;
            }
        }

        return 0;
    }
}
