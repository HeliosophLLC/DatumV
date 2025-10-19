using DatumIngest.Serialization;

namespace DatumIngest.Catalog;

/// <summary>
/// Central authority for file format detection and naming conventions. Detects
/// the provider name for a data file using file extension, filename patterns,
/// and magic byte signatures. Also derives logical table names and sidecar paths
/// so that every consumer agrees on the same conventions.
/// </summary>
public static class FileFormatDetector
{
    /// <summary>
    /// Maximum number of bytes read from the file header for magic byte detection.
    /// </summary>
    private const int MagicByteBufferLength = 8;

    /// <summary>
    /// Extensions whose files are container/storage formats wrapping another logical
    /// data file. The extension is stripped when deriving the logical table name and
    /// when computing sidecar base paths.
    /// </summary>
    private static readonly HashSet<string> ContainerExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".datum",
        ".gz",
    };

    /// <summary>
    /// Extensions that represent a compression wrapper rather than a data format.
    /// These are stripped to reveal the inner format extension during detection.
    /// </summary>
    private static readonly Dictionary<string, CompressionKind> CompressionExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".gz"] = CompressionKind.Gzip,
        };

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
        [".datum"] = "datum",
    };

    /// <summary>
    /// Glob patterns that match all file types recognised by auto-discovery.
    /// Generated from <see cref="ExtensionMap"/> plus MNIST IDX filename patterns.
    /// Use this instead of maintaining separate pattern lists in each consumer.
    /// </summary>
    public static IReadOnlyList<string> SupportedFilePatterns { get; } = BuildSupportedPatterns();

    /// <summary>
    /// Comma-separated list of the distinct provider/format names recognised by the
    /// detector, suitable for inclusion in user-facing error messages.
    /// </summary>
    public static string SupportedFormatList { get; } = BuildFormatList();

    /// <summary>
    /// Derives the logical table name for a data file. The file extension is
    /// replaced with an underscore so the name is a valid bare SQL identifier
    /// (e.g. <c>orders.csv</c> → <c>orders_csv</c>). Container formats have
    /// their storage extension stripped first (<c>orders.csv.datum</c> →
    /// <c>orders_csv</c>). Dots in the stem are also replaced with underscores.
    /// </summary>
    /// <param name="filePath">Absolute or relative path to the data file.</param>
    /// <returns>The logical table name for use in SQL FROM clauses.</returns>
    public static string DeriveTableName(string filePath)
    {
        string fileName = Path.GetFileName(filePath);

        // Strip container extensions (e.g. .datum) to reveal the logical name.
        string extension = Path.GetExtension(fileName);
        if (extension.Length > 0 && ContainerExtensions.Contains(extension))
        {
            fileName = Path.GetFileNameWithoutExtension(fileName);
        }

        return fileName.Replace('.', '_');
    }

    /// <summary>
    /// Returns the base path used for resolving sidecar files (<c>.datum-index</c>,
    /// <c>.datum-manifest</c>, <c>.datum-schema</c>). For container formats the
    /// storage extension is stripped so sidecars sit alongside the logical data name
    /// (<c>foo.csv.datum</c> → <c>foo.csv</c>, yielding <c>foo.csv.datum-index</c>).
    /// </summary>
    /// <param name="filePath">Absolute or relative path to the data file.</param>
    /// <returns>The base path to which sidecar suffixes should be appended.</returns>
    public static string GetSidecarBasePath(string filePath)
    {
        string extension = Path.GetExtension(filePath);
        if (extension.Length > 0 && ContainerExtensions.Contains(extension))
        {
            return filePath[..^extension.Length];
        }

        return filePath;
    }

    /// <summary>
    /// Detects the provider name and compression for the given file path.
    /// Uses the double-extension convention for compressed files: the outer
    /// <c>.gz</c> extension is stripped and the inner extension identifies
    /// the data format (e.g. <c>data.csv.gz</c> → provider "csv", compression Gzip).
    /// </summary>
    /// <param name="filePath">Absolute or relative path to the data file.</param>
    /// <returns>
    /// A <see cref="DetectedFormat"/> with provider name and compression kind,
    /// or <c>null</c> if the format cannot be determined.
    /// </returns>
    public static DetectedFormat? DetectFormat(string filePath)
    {
        string extension = Path.GetExtension(filePath);

        // If the outer extension is a compression format, strip it and detect the inner format.
        if (extension.Length > 0 && CompressionExtensions.TryGetValue(extension, out CompressionKind compression))
        {
            string innerPath = filePath[..^extension.Length];
            string? innerProvider = DetectProvider(innerPath);
            if (innerProvider is not null)
            {
                return new DetectedFormat(innerProvider, compression);
            }

            return null;
        }

        string? provider = DetectProvider(filePath);
        return provider is not null ? new DetectedFormat(provider, CompressionKind.None) : null;
    }

    /// <summary>
    /// Attempts to detect the provider name for the given file path.
    /// Uses a three-tier strategy: file extension, filename pattern
    /// (for MNIST-style IDX files), then magic byte signatures.
    /// Does not handle compression — use <see cref="DetectFormat"/> for
    /// combined provider and compression detection.
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

    /// <summary>
    /// Builds the glob pattern list from <see cref="ExtensionMap"/> keys plus
    /// MNIST IDX filename patterns, ensuring it stays in sync automatically.
    /// </summary>
    private static string[] BuildSupportedPatterns()
    {
        HashSet<string> patterns = new(StringComparer.OrdinalIgnoreCase);
        foreach (string extension in ExtensionMap.Keys)
        {
            patterns.Add($"*{extension}");
        }

        // MNIST-style IDX filenames that have no conventional extension.
        patterns.Add("*idx1-ubyte");
        patterns.Add("*idx2-ubyte");
        patterns.Add("*idx3-ubyte");
        patterns.Add("*idx4-ubyte");

        // Gzip-compressed variants of every known extension.
        foreach (string compressionExtension in CompressionExtensions.Keys)
        {
            foreach (string dataExtension in ExtensionMap.Keys)
            {
                patterns.Add($"*{dataExtension}{compressionExtension}");
            }
        }

        string[] result = [.. patterns];
        Array.Sort(result, StringComparer.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>
    /// Builds a comma-separated list of distinct provider names from <see cref="ExtensionMap"/>
    /// plus "idx" (for MNIST-style files matched by filename pattern).
    /// </summary>
    private static string BuildFormatList()
    {
        SortedSet<string> providers = new(StringComparer.OrdinalIgnoreCase);
        foreach (string provider in ExtensionMap.Values)
        {
            providers.Add(provider);
        }

        providers.Add("idx");
        return string.Join(", ", providers);
    }
}
