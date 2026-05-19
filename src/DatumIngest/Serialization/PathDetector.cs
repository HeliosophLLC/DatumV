namespace Heliosoph.DatumV.Serialization;

/// <summary>
/// Path conventions for ingestion: deriving SQL-safe table names from data file
/// paths, and locating sidecar files (<c>.datum-index</c>, <c>.datum-manifest</c>)
/// next to a primary <c>.datum</c> output.
/// </summary>
/// <remarks>
/// Minimal successor to the CLI's <c>FileFormatDetector</c>. The ingestion pipeline
/// only needs table-name derivation and sidecar path construction; provider
/// detection, directory scanning, and magic-byte matching belong to the
/// command-line / discovery layer that's being rebuilt on top of the gRPC server.
/// </remarks>
public static class PathDetector
{
    /// <summary>
    /// File extensions that wrap a logical data file and should be stripped when
    /// deriving names or sidecar base paths. Matches the convention where
    /// <c>orders.csv.datum</c> is a <c>.datum</c> container over the logical
    /// <c>orders.csv</c>, and <c>orders.csv.gz</c> is a gzip-compressed CSV.
    /// </summary>
    private static readonly HashSet<string> ContainerExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".datum",
        ".gz",
    };

    /// <summary>
    /// Derives a SQL-safe table name from a data file path. The file extension is
    /// replaced with an underscore so the result is a valid bare identifier
    /// (<c>orders.csv</c> → <c>orders_csv</c>). Container extensions (<c>.datum</c>,
    /// <c>.gz</c>) are stripped first so <c>orders.csv.datum</c> → <c>orders_csv</c>.
    /// Dots in the stem are also replaced with underscores.
    /// </summary>
    public static string DeriveTableName(string filePath)
    {
        string fileName = Path.GetFileName(filePath);

        string extension = Path.GetExtension(fileName);
        if (extension.Length > 0 && ContainerExtensions.Contains(extension))
        {
            fileName = Path.GetFileNameWithoutExtension(fileName);
        }

        return fileName.Replace('.', '_');
    }

    /// <summary>
    /// Returns the base path used for resolving sidecar files. For container
    /// formats the outer extension is stripped so sidecars sit alongside the
    /// logical data name (<c>foo.csv.datum</c> → <c>foo.csv</c>, yielding
    /// <c>foo.csv.datum-index</c> when the sidecar suffix is appended).
    /// </summary>
    public static string GetSidecarBasePath(string filePath)
    {
        string extension = Path.GetExtension(filePath);
        if (extension.Length > 0 && ContainerExtensions.Contains(extension))
        {
            return filePath[..^extension.Length];
        }

        return filePath;
    }
}
