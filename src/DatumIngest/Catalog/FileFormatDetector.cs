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
    /// Reads the first few bytes of a file and attempts to identify its format
    /// via well-known magic byte signatures.
    /// </summary>
    private static string? DetectFromMagicBytes(string filePath)
    {
        Span<byte> buffer = stackalloc byte[MagicByteBufferLength];

        using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        int bytesRead = stream.Read(buffer);

        if (bytesRead < 4)
        {
            return null;
        }

        // Parquet: "PAR1" magic at start of file.
        if (buffer[0] == 'P' && buffer[1] == 'A' && buffer[2] == 'R' && buffer[3] == '1')
        {
            return "parquet";
        }

        // HDF5: 8-byte signature "\x89HDF\r\n\x1a\n".
        if (bytesRead >= 8
            && buffer[0] == 0x89 && buffer[1] == 'H' && buffer[2] == 'D' && buffer[3] == 'F'
            && buffer[4] == 0x0D && buffer[5] == 0x0A && buffer[6] == 0x1A && buffer[7] == 0x0A)
        {
            return "hdf5";
        }

        // ZIP: local file header "PK\x03\x04".
        if (buffer[0] == 'P' && buffer[1] == 'K' && buffer[2] == 0x03 && buffer[3] == 0x04)
        {
            return "zip";
        }

        // IDX: first two bytes 0x00, third byte is a valid type code, fourth is dimension count >= 1.
        if (buffer[0] == 0x00 && buffer[1] == 0x00 && IsValidIdxTypeCode(buffer[2]) && buffer[3] >= 1)
        {
            return "idx";
        }

        // JSON: first non-whitespace byte is '{' or '['.
        byte firstNonWhitespace = FindFirstNonWhitespace(buffer[..bytesRead]);
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
