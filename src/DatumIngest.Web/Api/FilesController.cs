using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Web.Dtos.Files;
using Microsoft.AspNetCore.Mvc;

namespace DatumIngest.Web.Api;

/// <summary>
/// Read-only endpoint backing the Project Explorer side panel. Returns one
/// row per file under the catalog root, classified by kind and joined
/// against the in-memory registries so orphan files surface.
/// </summary>
/// <remarks>
/// <para>
/// Walks the catalog directory directly + classifies via
/// <see cref="SystemFilesProvider.ClassifyPath"/> rather than calling the
/// provider's <c>ScanAsync</c>. The scan path materialises rows through the
/// pool's RowBatch / Arena machinery — each rental reserves 8 GB of
/// virtual address space, and a REST endpoint that doesn't return batches
/// to the pool exhausts process VA after a handful of refetches. Walking
/// directly stays out of the Arena lifecycle entirely; the SQL path
/// (<c>SELECT * FROM system.files</c>) keeps the pooled scan because the
/// query operators finalise batches correctly.
/// </para>
/// <para>
/// Live updates reuse <see cref="DatumIngest.Web.Hubs.CatalogHub"/>: the
/// client refetches on Function/Procedure/Model/Table/Index events plus
/// any <c>OnFilesChanged</c> push from the directory watcher.
/// </para>
/// </remarks>
[ApiController]
[Route("api/files")]
public sealed class FilesController(TableCatalog catalog) : ControllerBase
{
    /// <summary>
    /// Returns every file under the catalog root with kind classification,
    /// size, modification time, and orphan status.
    /// </summary>
    [HttpGet]
    public ActionResult<FilesDto> GetFiles(CancellationToken cancellationToken)
    {
        string? catalogDirectory = catalog.CatalogDirectory;
        if (string.IsNullOrEmpty(catalogDirectory) || !Directory.Exists(catalogDirectory))
        {
            return new FilesDto([]);
        }

        HashSet<string> referenced = SystemFilesProvider.BuildReferencedPaths(catalog);

        List<FileEntryDto> entries = [];
        foreach (string fullPath in Directory
            .EnumerateFiles(catalogDirectory, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string relativePath = Path
                .GetRelativePath(catalogDirectory, fullPath)
                .Replace('\\', '/');
            (string kind, string? schema, string? name) =
                SystemFilesProvider.ClassifyPath(relativePath);

            FileInfo info;
            try
            {
                info = new FileInfo(fullPath);
            }
            catch
            {
                // File may have been removed between enumeration and stat;
                // skip rather than abort the whole scan.
                continue;
            }

            bool isOrphan = SystemFilesProvider.IsManagedKind(kind)
                && !referenced.Contains(relativePath);

            entries.Add(new FileEntryDto(
                Path: relativePath,
                Kind: kind,
                Schema: schema,
                Name: name,
                SizeBytes: info.Length,
                ModifiedAt: new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                IsOrphan: isOrphan));
        }

        return new FilesDto(entries);
    }
}
