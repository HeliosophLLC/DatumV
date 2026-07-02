using Heliosoph.DatumV.DatasetLibrary;
using Heliosoph.DatumV.ModelLibrary;
using Microsoft.AspNetCore.Mvc;

// IManifestStore exists in both Heliosoph.DatumV.DatasetLibrary and
// Heliosoph.DatumV.ModelLibrary (each catalog owns its own); this controller
// reaches the dataset one.
using IManifestStore = Heliosoph.DatumV.DatasetLibrary.IManifestStore;

namespace Heliosoph.DatumV.Web.Api;

[ApiController]
[Route("api/dataset-catalog")]
public sealed class DatasetCatalogController(
    IManifestStore store,
    ILicenseRegistry licenseRegistry,
    ILicenseAcceptanceService licenses,
    IDatasetDownloadService downloads,
    IDatasetPathResolver paths) : ControllerBase
{
    // GET /api/dataset-catalog — the full dataset manifest. Front-end
    // fetches once at startup and caches in a Valtio proxy.
    [HttpGet]
    public DatasetCatalogManifest GetManifest() => store.Manifest;

    // GET /api/dataset-catalog/licenses/{id}/text — raw license text for
    // acceptance UI. Returns 404 if id unknown or textFile missing.
    [HttpGet("licenses/{id}/text")]
    [Produces("text/plain")]
    public ActionResult<string> GetLicenseText(string id)
    {
        string? text = licenseRegistry.GetText(id);
        if (text is null) return NotFound();
        return Content(text, "text/plain");
    }

    // GET /api/dataset-catalog/entries/{name}/card — raw markdown body
    // of the entry's card. 404 when the entry didn't declare a
    // CardFile.
    [HttpGet("entries/{name}/card")]
    [Produces("text/markdown")]
    public ActionResult<string> GetEntryCard(string name)
    {
        string? text = store.GetEntryCardMarkdown(name);
        if (text is null) return NotFound();
        return Content(text, "text/markdown");
    }

    // GET /api/dataset-catalog/recipes/{**path} — raw SQL body of an
    // ingest recipe declared as a variant's `sqlFile` (e.g.
    // `mnist/split.sql`). Powers the "View recipe" section on the detail
    // card. 404 when the path isn't a declared recipe or the file is
    // missing; only declared paths are serveable.
    [HttpGet("recipes/{**path}")]
    [Produces("text/plain")]
    public ActionResult<string> GetRecipeSql(string path)
    {
        string? sql = store.GetRecipeSql(path);
        if (sql is null) return NotFound();
        return Content(sql, "text/plain");
    }

    // GET /api/dataset-catalog/entries/{name}/card/assets/{**path} —
    // serves screenshots / diagrams / sample-image strips referenced
    // from the entry's card markdown. Path resolves against the
    // manifest's `cards/<entry-card-basename>/` subtree with a
    // traversal guard; anything escaping 404s.
    [HttpGet("entries/{name}/card/assets/{**path}")]
    public ActionResult GetEntryCardAsset(string name, string path)
    {
        string? abs = store.ResolveEntryAssetPath(name, path);
        if (abs is null) return NotFound();
        return PhysicalFile(abs, ContentTypeForFile(abs));
    }

    // GET /api/dataset-catalog/entries/{name}/hero — serves the entry's
    // hero image. 404 when the entry didn't declare HeroImageFile or
    // the file isn't present on disk.
    [HttpGet("entries/{name}/hero")]
    public ActionResult GetHeroImage(string name)
    {
        string? abs = store.ResolveHeroImagePath(name);
        if (abs is null) return NotFound();
        return PhysicalFile(abs, ContentTypeForFile(abs));
    }

    private static string ContentTypeForFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            _ => "application/octet-stream",
        };
    }

    // POST /api/dataset-catalog/licenses/{id}/accept — records explicit
    // acceptance. Idempotent. Reuses the shared ILicenseAcceptanceService —
    // licenses accepted via the model catalog are recognised here and
    // vice versa.
    [HttpPost("licenses/{id}/accept")]
    public async Task<IActionResult> AcceptLicense(string id, CancellationToken ct)
    {
        if (licenseRegistry.GetMetadata(id) is null) return NotFound();
        await licenses.AcceptAsync(id, ct);
        return NoContent();
    }

    // GET /api/dataset-catalog/licenses/accepted — list of accepted license
    // ids. Returns the shared set used by both the model and dataset
    // catalogs.
    [HttpGet("licenses/accepted")]
    public async Task<IReadOnlyList<string>> GetAcceptedLicenses(CancellationToken ct)
        => await licenses.GetAcceptedAsync(ct);

    // GET /api/dataset-catalog/datasets/{id}/state — fast probe of
    // local-vs-remote.
    [HttpGet("datasets/{id}/state")]
    public async Task<ActionResult<DatasetInstallState>> GetState(string id, CancellationToken ct)
    {
        try
        {
            return await downloads.ProbeAsync(id, ct);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    // GET /api/dataset-catalog/states — bulk probe: one call returns the
    // install state for every dataset in the manifest. Used by the
    // Datasets view to render install-state badges without N round-trips.
    [HttpGet("states")]
    public async Task<IReadOnlyDictionary<string, DatasetInstallState>> GetStates(CancellationToken ct)
        => await downloads.ProbeAllAsync(ct);

    // POST /api/dataset-catalog/datasets/{id}/install — kicks off background
    // download + ingest. Returns 202 immediately; progress flows over
    // the SignalR hub.
    //   - 404 if id unknown
    //   - 409 if an install is already running
    //   - 412 if a required license has not been accepted
    [HttpPost("datasets/{id}/install")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status412PreconditionFailed)]
    public async Task<IActionResult> Install(string id, CancellationToken ct)
    {
        try
        {
            await downloads.InstallAsync(id, ct);
            return Accepted();
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (LicenseNotAcceptedException ex)
        {
            return StatusCode(StatusCodes.Status412PreconditionFailed,
                new { error = "license_not_accepted", licenseId = ex.LicenseId, message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = "install_blocked", message = ex.Message });
        }
    }

    // DELETE /api/dataset-catalog/datasets/{id} — removes the ingested
    // copy under the catalog root. Raw-cache contents are left alone;
    // a separate purge surface clears them.
    [HttpDelete("datasets/{id}")]
    public async Task<IActionResult> Uninstall(string id, CancellationToken ct)
    {
        try
        {
            await downloads.UninstallAsync(id, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    // GET /api/dataset-catalog/partial-bytes — bulk view of partial
    // download state under the raw cache. Returns one entry per dataset
    // with non-zero .part bytes; datasets with none are omitted.
    [HttpGet("partial-bytes")]
    public async Task<IReadOnlyDictionary<string, long>> GetAllPartialBytes(CancellationToken ct)
        => await downloads.GetAllPartialBytesAsync(ct);

    // DELETE /api/dataset-catalog/datasets/{id}/partials — wipes any .part
    // files in the dataset's raw-cache folder.
    [HttpDelete("datasets/{id}/partials")]
    public async Task<IActionResult> DeletePartials(string id, CancellationToken ct)
    {
        try
        {
            await downloads.DeletePartialsAsync(id, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    // GET /api/dataset-catalog/on-disk-versions — bulk view of which
    // ingested version folders exist per variant. Keyed by variant.id
    // (the install handle); each value is the list of on-disk version
    // folder names that match a declared version on that variant.
    [HttpGet("on-disk-versions")]
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetOnDiskVersions()
    {
        Dictionary<string, IReadOnlyList<string>> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (DatasetEntry entry in store.Manifest.Datasets)
        {
            foreach (DatasetVariant variant in entry.Variants)
            {
                string idRoot = Path.Combine(paths.IngestedDatasetsRoot, variant.Id);
                if (!Directory.Exists(idRoot)) continue;

                HashSet<string> declared = new(StringComparer.Ordinal);
                foreach (CatalogDatasetVersion v in variant.Versions) declared.Add(v.Version);

                List<string> onDisk = [];
                foreach (string dir in Directory.EnumerateDirectories(idRoot))
                {
                    string name = Path.GetFileName(dir);
                    if (declared.Contains(name)) onDisk.Add(name);
                }
                if (onDisk.Count > 0) result[variant.Id] = onDisk;
            }
        }
        return result;
    }
}
