using DatumIngest.Catalog;

namespace DatumIngest.Compute.Services;

/// <summary>
/// Builds a <see cref="TableCatalog"/> from a local dataset directory by
/// registering all built-in providers and auto-discovering supported files.
/// </summary>
internal static class DatasetCatalogFactory
{
    /// <summary>
    /// Creates a <see cref="TableCatalog"/> populated with every supported
    /// file found in <paramref name="directoryPath"/>. Each file becomes a
    /// table whose name is derived by <see cref="FileFormatDetector.DeriveTableName"/>.
    /// </summary>
    /// <param name="directoryPath">Local directory containing dataset files.</param>
    /// <returns>A fully configured catalog ready for query execution.</returns>
    public static async Task<TableCatalog> CreateAsync(string directoryPath)
    {
        TableCatalog catalog = new();

        foreach (string pattern in FileFormatDetector.SupportedFilePatterns)
        {
            foreach (string filePath in Directory.EnumerateFiles(directoryPath, pattern))
            {
                string tableName = FileFormatDetector.DeriveTableName(filePath);

                if (catalog.TryResolve(tableName, out _))
                {
                    continue;
                }

                catalog.Register(filePath);
            }
        }

        await catalog.ExpandMultiTableSourcesAsync(CancellationToken.None).ConfigureAwait(false);

        catalog.DiscoverSidecars();

        return catalog;
    }
}
