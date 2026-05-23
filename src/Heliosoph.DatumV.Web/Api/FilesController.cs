using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Providers;
using Heliosoph.DatumV.Web.Dtos.Files;
using Microsoft.AspNetCore.Mvc;

namespace Heliosoph.DatumV.Web.Api;

/// <summary>
/// File-system surface for the editor + Project Explorer. <see cref="GetFiles"/>
/// drives the explorer tree (one row per file, classified by kind, joined
/// with the registries so orphans surface). The contents + state endpoints
/// back the file-backed tab model: SQL tabs read and write `.sql` files
/// under the catalog, and the pane tree itself persists to
/// <c>.datumv/tabs.json</c> per catalog.
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
/// Live updates reuse <see cref="Heliosoph.DatumV.Web.Hubs.CatalogHub"/>: the
/// client refetches on Function/Procedure/Model/Table/Index events plus
/// any <c>OnFilesChanged</c> push from the directory watcher.
/// </para>
/// </remarks>
[ApiController]
[Route("api/files")]
public sealed class FilesController(TableCatalog catalog) : ControllerBase
{
    // Per-catalog state directory. Holds tabs.json and any future
    // editor-state we want to round-trip across catalog opens without
    // checking it in. Hidden from the Project Explorer's default view
    // via the front-end SYSTEM_PATH_PREFIXES list.
    private const string StateDirName = ".datumv";
    private const string TabsStateFileName = "tabs.json";

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

    /// <summary>
    /// Catalog directory path. The renderer uses this as the default root
    /// for the native save dialog so Ctrl+S on a scratch tab opens inside
    /// the user's catalog rather than wherever the OS last remembered.
    /// </summary>
    [HttpGet("root")]
    public ActionResult<CatalogRootDto> GetRoot()
    {
        string? dir = catalog.CatalogDirectory;
        if (string.IsNullOrEmpty(dir))
        {
            return NotFound();
        }
        return new CatalogRootDto(dir);
    }

    /// <summary>
    /// Reads a file's UTF-8 text contents. <paramref name="path"/> is
    /// catalog-relative (forward slashes); traversal outside the catalog
    /// root is rejected with 400.
    /// </summary>
    [HttpGet("contents")]
    public async Task<ActionResult<FileContentsDto>> GetContents(
        [FromQuery] string path,
        CancellationToken cancellationToken)
    {
        if (!TryResolveCatalogPath(path, out string? fullPath, out ActionResult? error))
        {
            return error;
        }
        if (!System.IO.File.Exists(fullPath))
        {
            return NotFound();
        }
        string contents = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, cancellationToken);
        DateTimeOffset modifiedAt = new(System.IO.File.GetLastWriteTimeUtc(fullPath), TimeSpan.Zero);
        return new FileContentsDto(contents, modifiedAt);
    }

    /// <summary>
    /// Writes a file's UTF-8 text contents. Creates missing parent
    /// directories. <paramref name="path"/> is catalog-relative; traversal
    /// outside the catalog root is rejected with 400.
    /// </summary>
    [HttpPut("contents")]
    public async Task<ActionResult<FileContentsResponseDto>> PutContents(
        [FromQuery] string path,
        [FromBody] FileContentsRequestDto body,
        CancellationToken cancellationToken)
    {
        if (!TryResolveCatalogPath(path, out string? fullPath, out ActionResult? error))
        {
            return error;
        }

        string? parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        await System.IO.File.WriteAllTextAsync(fullPath, body.Contents ?? string.Empty, Encoding.UTF8, cancellationToken);

        DateTimeOffset modifiedAt = new(System.IO.File.GetLastWriteTimeUtc(fullPath), TimeSpan.Zero);

        return new FileContentsResponseDto(modifiedAt);
    }

    /// <summary>
    /// Deletes a file or directory under the catalog root. Directories are
    /// removed recursively so the Project Explorer's right-click "Delete"
    /// can target a folder without forcing the user to empty it first.
    /// </summary>
    [HttpDelete("contents")]
    public ActionResult DeleteContents([FromQuery] string path)
    {
        if (!TryResolveCatalogPath(path, out string? fullPath, out ActionResult? error))
        {
            return error;
        }
        // Refuse to delete the catalog root itself — a relative path of
        // "" or "." normalises to the root and would wipe the entire
        // catalog. TryResolveCatalogPath already rejects empty input;
        // this guards the edge case where a single-segment path resolves
        // back to the root.
        if (string.Equals(fullPath, Path.GetFullPath(catalog.CatalogDirectory!), StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("cannot delete catalog root");
        }
        if (System.IO.File.Exists(fullPath))
        {
            System.IO.File.Delete(fullPath);
            return NoContent();
        }
        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
            return NoContent();
        }
        return NotFound();
    }

    /// <summary>
    /// Renames or moves a file or directory under the catalog root. Both
    /// endpoints must stay catalog-relative. Refuses to overwrite an
    /// existing destination so a typo can't silently clobber another file.
    /// </summary>
    [HttpPost("rename")]
    public ActionResult RenameFile([FromBody] FileRenameRequestDto body)
    {
        if (!TryResolveCatalogPath(body.FromPath, out string? fromFull, out ActionResult? fromErr))
        {
            return fromErr;
        }
        if (!TryResolveCatalogPath(body.ToPath, out string? toFull, out ActionResult? toErr))
        {
            return toErr;
        }
        if (string.Equals(fromFull, toFull, StringComparison.OrdinalIgnoreCase))
        {
            return NoContent();
        }
        if (System.IO.File.Exists(toFull) || Directory.Exists(toFull))
        {
            return Conflict("destination already exists");
        }
        string? parent = Path.GetDirectoryName(toFull);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
        if (System.IO.File.Exists(fromFull))
        {
            System.IO.File.Move(fromFull, toFull);
            return NoContent();
        }
        if (Directory.Exists(fromFull))
        {
            Directory.Move(fromFull, toFull);
            return NoContent();
        }
        return NotFound();
    }

    /// <summary>
    /// Reads the per-catalog tabs state from <c>.datumv/tabs.json</c>.
    /// Returns 204 (no content) when the file does not exist — the
    /// renderer treats that as "fresh catalog, start with one Untitled
    /// tab." Body shape is opaque to the server; clients own the schema.
    /// </summary>
    [HttpGet("state/tabs")]
    public async Task<ActionResult<string>> GetTabsState(CancellationToken cancellationToken)
    {
        string? dir = catalog.CatalogDirectory;
        if (string.IsNullOrEmpty(dir))
        {
            return NotFound();
        }

        string fullPath = Path.Combine(dir, StateDirName, TabsStateFileName);
        if (!System.IO.File.Exists(fullPath))
        {
            return NoContent();
        }

        string contents = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, cancellationToken);
        
        return Content(contents, "application/json", Encoding.UTF8);
    }

    /// <summary>
    /// Writes the per-catalog tabs state to <c>.datumv/tabs.json</c>.
    /// Lazily creates the state directory and appends <c>.datumv/</c> to
    /// <c>.gitignore</c> if the entry isn't already present. Body is
    /// passed through as raw JSON — the server doesn't interpret the
    /// pane-tree shape, so the client owns the schema.
    /// </summary>
    [HttpPut("state/tabs")]
    public async Task<ActionResult> PutTabsState(
        [FromBody] JsonElement body,
        CancellationToken cancellationToken)
    {
        string? dir = catalog.CatalogDirectory;
        if (string.IsNullOrEmpty(dir))
        {
            return NotFound();
        }

        string stateDir = Path.Combine(dir, StateDirName);
        Directory.CreateDirectory(stateDir);
        await EnsureStateDirGitignoredAsync(dir, cancellationToken);

        string fullPath = Path.Combine(stateDir, TabsStateFileName);
        // GetRawText preserves whatever JSON the client posted — handy if
        // they pretty-printed it; the file is meant to be human-readable.
        await System.IO.File.WriteAllTextAsync(
            fullPath, body.GetRawText(), Encoding.UTF8, cancellationToken);
        return NoContent();
    }

    // Resolves a catalog-relative path to an absolute path under the
    // catalog root. Rejects empty paths, absolute inputs, and any input
    // that escapes the root via `..`. The resolved path may or may not
    // exist — callers handle existence separately.
    private bool TryResolveCatalogPath(
        string path,
        [NotNullWhen(true)] out string? fullPath,
        [NotNullWhen(false)] out ActionResult? error)
    {
        fullPath = null;
        error = null;
        string? root = catalog.CatalogDirectory;
        if (string.IsNullOrEmpty(root))
        {
            error = NotFound();
            return false;
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            error = BadRequest("path is required");
            return false;
        }
        // Normalize separators + reject absolute inputs / drive letters.
        string normalized = path.Replace('\\', '/');
        if (Path.IsPathRooted(normalized) || normalized.Contains(':'))
        {
            error = BadRequest("path must be catalog-relative");
            return false;
        }
        string combined = Path.GetFullPath(Path.Combine(root, normalized));
        string rootFull = Path.GetFullPath(root);
        string rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(combined, rootFull, StringComparison.OrdinalIgnoreCase))
        {
            error = BadRequest("path escapes catalog root");
            return false;
        }
        fullPath = combined;
        return true;
    }

    // Appends `.datumv/` to the catalog's .gitignore if it isn't already
    // listed. Idempotent — exact line match (trimmed) wins, so existing
    // glob entries that already cover it are also accepted. Creates the
    // file if missing.
    private static async Task EnsureStateDirGitignoredAsync(
        string catalogRoot,
        CancellationToken cancellationToken)
    {
        string gitignorePath = Path.Combine(catalogRoot, ".gitignore");
        string entry = StateDirName + "/";

        if (System.IO.File.Exists(gitignorePath))
        {
            string existing = await System.IO.File.ReadAllTextAsync(gitignorePath, Encoding.UTF8, cancellationToken);
            foreach (string line in existing.Split('\n'))
            {
                string trimmed = line.Trim().TrimEnd('\r');
                if (string.Equals(trimmed, entry, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(trimmed, StateDirName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            string suffix = existing.EndsWith('\n') ? string.Empty : "\n";
            await System.IO.File.AppendAllTextAsync(
                gitignorePath,
                suffix + entry + "\n",
                Encoding.UTF8,
                cancellationToken);
            return;
        }
        await System.IO.File.WriteAllTextAsync(
            gitignorePath,
            entry + "\n",
            Encoding.UTF8,
            cancellationToken);
    }
}
