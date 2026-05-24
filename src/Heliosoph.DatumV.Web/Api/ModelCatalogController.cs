using Heliosoph.DatumV.Catalog.Registries;
using Heliosoph.DatumV.ModelLibrary;
using Microsoft.AspNetCore.Mvc;

namespace Heliosoph.DatumV.Web.Api;

// Lightweight DTO describing one task contract for the front-end filter UI.
// Family is stringified (e.g. "ComputerVision") so the JSON wire form
// matches the `family` column of system.task_contracts and the locale-file
// keys (`filters.family.computerVision`, …) on the client.
public sealed record CatalogTaskInfo(
    string Name,
    string Family,
    string Description);

[ApiController]
[Route("api/model-catalog")]
public sealed class ModelCatalogController(
    IManifestStore store,
    ILicenseRegistry licenseRegistry,
    ILicenseAcceptanceService licenses,
    IModelDownloadService downloads,
    IModelPathResolver paths) : ControllerBase
{
    // GET /api/model-catalog — the full manifest. Front-end fetches once at
    // startup and caches in a Valtio proxy.
    [HttpGet]
    public CatalogManifest GetManifest() => store.Manifest;

    // GET /api/model-catalog/tasks — the engine-defined task vocabulary
    // (name + family + one-line description), one entry per
    // <see cref="TaskTypeRegistry"/> contract.
    [HttpGet("tasks")]
    public IReadOnlyList<CatalogTaskInfo> GetTasks()
    {
        var contracts = TaskTypeRegistry.Entries;
        var result = new CatalogTaskInfo[contracts.Count];
        for (int i = 0; i < contracts.Count; i++)
        {
            var c = contracts[i];
            result[i] = new CatalogTaskInfo(c.Name, c.Family.ToString(), c.Description);
        }
        return result;
    }

    // GET /api/model-catalog/licenses/{id}/text — raw license text for
    // acceptance UI. Returns 404 if id unknown or textFile missing.
    [HttpGet("licenses/{id}/text")]
    [Produces("text/plain")]
    public ActionResult<string> GetLicenseText(string id)
    {
        string? text = licenseRegistry.GetText(id);
        if (text is null) return NotFound();
        return Content(text, "text/plain");
    }

    // GET /api/model-catalog/entries/{name}/card — raw markdown body of
    // the entry card. Returns 404 when the entry didn't declare a cardFile.
    [HttpGet("entries/{name}/card")]
    [Produces("text/markdown")]
    public ActionResult<string> GetEntryCard(string name)
    {
        string? text = store.GetEntryCardMarkdown(name);
        if (text is null) return NotFound();
        return Content(text, "text/markdown");
    }

    // GET /api/model-catalog/entries/{name}/card/assets/{**path} — serves
    // screenshots / diagrams / etc. referenced from the entry-card markdown.
    [HttpGet("entries/{name}/card/assets/{**path}")]
    public ActionResult GetEntryCardAsset(string name, string path)
    {
        string? abs = store.ResolveEntryCardAssetPath(name, path);
        if (abs is null) return NotFound();
        return PhysicalFile(abs, ContentTypeForFile(abs));
    }

    // GET /api/model-catalog/entries/{name}/hero — serves the entry's
    // hero image. 404 when the entry didn't declare HeroImageFile or the
    // file isn't present on disk.
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

    // POST /api/model-catalog/licenses/{id}/accept — records explicit
    // acceptance. Idempotent.
    [HttpPost("licenses/{id}/accept")]
    public async Task<IActionResult> AcceptLicense(string id, CancellationToken ct)
    {
        if (licenseRegistry.GetMetadata(id) is null) return NotFound();
        await licenses.AcceptAsync(id, ct);
        return NoContent();
    }

    // GET /api/model-catalog/licenses/accepted — list of accepted license ids.
    [HttpGet("licenses/accepted")]
    public async Task<IReadOnlyList<string>> GetAcceptedLicenses(CancellationToken ct)
        => await licenses.GetAcceptedAsync(ct);

    // GET /api/model-catalog/variants/{id}/state — fast probe of local-vs-remote.
    [HttpGet("variants/{id}/state")]
    public async Task<ActionResult<ModelInstallState>> GetState(string id, CancellationToken ct)
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

    // GET /api/model-catalog/states — bulk probe: one call returns the
    // install state for every variant in the manifest.
    [HttpGet("states")]
    public async Task<IReadOnlyDictionary<string, ModelInstallState>> GetStates(CancellationToken ct)
        => await downloads.ProbeAllAsync(ct);

    // POST /api/model-catalog/variants/{id}/install — kicks off background
    // download. Returns 202 immediately; progress flows over the SignalR
    // hub.
    [HttpPost("variants/{id}/install")]
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

    // DELETE /api/model-catalog/variants/{id} — removes the local copy.
    [HttpDelete("variants/{id}")]
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

    // POST /api/model-catalog/variants/{id}/install/{version} — kicks off
    // a pinned background install for a specific catalog version.
    [HttpPost("variants/{id}/install/{version}")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status412PreconditionFailed)]
    public async Task<IActionResult> InstallPinned(
        string id, string version, CancellationToken ct)
    {
        try
        {
            await downloads.InstallPinnedAsync(id, version, ct);
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

    // POST /api/model-catalog/variants/{id}/activate/{version} — flips the
    // active pointer to a non-active on-disk version.
    [HttpPost("variants/{id}/activate/{version}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ActivateVersion(
        string id, string version, CancellationToken ct)
    {
        try
        {
            await downloads.ActivateVersionAsync(id, version, ct);
            return NoContent();
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = "activate_failed", message = ex.Message });
        }
    }

    // DELETE /api/model-catalog/variants/{id}/versions/{version} — removes
    // a single version's folder + drops the identifiers it registered.
    [HttpDelete("variants/{id}/versions/{version}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteVersion(
        string id, string version, CancellationToken ct)
    {
        try
        {
            await downloads.DeleteVersionAsync(id, version, ct);
            return NoContent();
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = "delete_failed", message = ex.Message });
        }
    }

    // GET /api/model-catalog/on-disk-versions — bulk view of which version
    // folders exist for each variant. Keyed by variant id.
    [HttpGet("on-disk-versions")]
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetOnDiskVersions()
    {
        Dictionary<string, IReadOnlyList<string>> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (CatalogEntry entry in store.Manifest.Entries)
        {
            foreach (CatalogVariant variant in entry.Variants)
            {
                string idRoot = Path.Combine(paths.ModelsRoot, variant.Id);
                if (!Directory.Exists(idRoot)) continue;

                HashSet<string> declared = new(StringComparer.Ordinal);
                foreach (CatalogVersion v in variant.Versions) declared.Add(v.Version);

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

    // GET /api/model-catalog/active-versions — one entry per installed
    // variant, mapping variant id → the version string written into
    // <DATUMV_MODELS>/<variant-id>/active.
    [HttpGet("active-versions")]
    public IReadOnlyDictionary<string, string> GetActiveVersions()
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (CatalogEntry entry in store.Manifest.Entries)
        {
            foreach (CatalogVariant variant in entry.Variants)
            {
                string? active = paths.GetActiveVersion(variant.Id);
                if (active is not null) result[variant.Id] = active;
            }
        }
        return result;
    }

    // GET /api/model-catalog/partial-bytes — bulk view of partial download
    // state. Keyed by variant id.
    [HttpGet("partial-bytes")]
    public async Task<IReadOnlyDictionary<string, long>> GetAllPartialBytes(CancellationToken ct)
        => await downloads.GetAllPartialBytesAsync(ct);

    // DELETE /api/model-catalog/variants/{id}/partials — wipes any .part
    // files for the variant.
    [HttpDelete("variants/{id}/partials")]
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
}
