using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Indexing;
using DatumIngest.Manifest;

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
    public static async Task<TableCatalog> CreateAsync(string directoryPath)
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

        await catalog.ExpandMultiTableSourcesAsync(CancellationToken.None).ConfigureAwait(false);

        DiscoverSidecarIndexes(catalog);
        DiscoverSidecarManifests(catalog);

        return catalog;
    }

    /// <summary>
    /// Auto-discovers <c>.datum-index</c> sidecar files for all registered tables
    /// and attaches them to the catalog for chunk-based pruning.
    /// </summary>
    private static void DiscoverSidecarIndexes(TableCatalog catalog)
    {
        IndexReader reader = new();
        HashSet<string> loadedPaths = new(StringComparer.OrdinalIgnoreCase);

        foreach (string tableName in catalog.TableNames)
        {
            TableDescriptor descriptor = catalog.Resolve(tableName);
            string sidecarPath = descriptor.FilePath + ".datum-index";

            if (!File.Exists(sidecarPath) || !loadedPaths.Add(sidecarPath))
            {
                continue;
            }

            using FileStream stream = File.OpenRead(sidecarPath);
            SourceIndexSet indexSet = reader.Read(stream);

            // Register per-table indexes for all descriptors sharing this source file.
            foreach (string name in catalog.TableNames)
            {
                TableDescriptor d = catalog.Resolve(name);
                if (!string.Equals(d.FilePath, descriptor.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (indexSet.Tables.TryGetValue(name, out SourceIndex? index))
                {
                    catalog.RegisterIndex(name, index);
                }
            }
        }
    }

    /// <summary>
    /// Auto-discovers <c>.datum-manifest</c> sidecar files for all registered tables
    /// and attaches them to the catalog for statistics-driven cardinality estimation.
    /// </summary>
    private static void DiscoverSidecarManifests(TableCatalog catalog)
    {
        HashSet<string> loadedPaths = new(StringComparer.OrdinalIgnoreCase);

        foreach (string tableName in catalog.TableNames)
        {
            TableDescriptor descriptor = catalog.Resolve(tableName);
            string sidecarPath = descriptor.FilePath + ".datum-manifest";

            if (!File.Exists(sidecarPath) || !loadedPaths.Add(sidecarPath))
            {
                continue;
            }

            string json = File.ReadAllText(sidecarPath);
            SourceManifest? sourceManifest = ManifestSerializer.Deserialize(json);

            if (sourceManifest is null)
            {
                continue;
            }

            // Register per-table manifests for all descriptors sharing this source file.
            foreach (string name in catalog.TableNames)
            {
                TableDescriptor d = catalog.Resolve(name);
                if (!string.Equals(d.FilePath, descriptor.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (sourceManifest.Tables.TryGetValue(name, out QueryResultsManifest? manifest))
                {
                    catalog.RegisterManifest(name, manifest);
                }
            }
        }
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
