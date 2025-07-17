using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;

namespace DatumIngest.Compute.Services;

/// <summary>
/// Builds a <see cref="TableCatalog"/> from a local dataset directory by
/// registering all built-in providers and auto-discovering supported files.
/// </summary>
internal static class DatasetCatalogFactory
{
    /// <summary>
    /// File extensions recognised by auto-discovery. Matches the formats
    /// supported by <see cref="FileFormatDetector"/>.
    /// </summary>
    private static readonly string[] SupportedExtensions =
    [
        "*.csv", "*.tsv",
        "*.json",
        "*.jsonl", "*.ndjson",
        "*.parquet", "*.pq",
        "*.hdf5", "*.h5", "*.hdf",
        "*.zip",
        "*.idx",
    ];

    /// <summary>
    /// Creates a <see cref="TableCatalog"/> populated with every supported
    /// file found in <paramref name="directoryPath"/>. Each file becomes a
    /// table whose name is the filename without extension.
    /// </summary>
    /// <param name="directoryPath">Local directory containing dataset files.</param>
    /// <returns>A fully configured catalog ready for query execution.</returns>
    public static TableCatalog Create(string directoryPath)
    {
        TableCatalog catalog = new();

        RegisterProviders(catalog);

        foreach (string pattern in SupportedExtensions)
        {
            foreach (string filePath in Directory.EnumerateFiles(directoryPath, pattern))
            {
                string tableName = Path.GetFileNameWithoutExtension(filePath);

                // Skip duplicates — first file wins when multiple extensions
                // produce the same table name (e.g. data.csv and data.json).
                if (catalog.TryResolve(tableName, out _))
                {
                    continue;
                }

                catalog.Register(tableName, filePath);
            }
        }

        return catalog;
    }

    /// <summary>
    /// Registers all built-in provider factories on the catalog.
    /// </summary>
    private static void RegisterProviders(TableCatalog catalog)
    {
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.RegisterProvider("json", () => new JsonTableProvider());
        catalog.RegisterProvider("jsonl", () => new JsonlTableProvider());
        catalog.RegisterProvider("zip", () => new ZipTableProvider());
        catalog.RegisterProvider("hdf5", () => new Hdf5TableProvider());
        catalog.RegisterProvider("parquet", () => new ParquetTableProvider());
        catalog.RegisterProvider("idx", () => new IdxTableProvider());
    }
}
