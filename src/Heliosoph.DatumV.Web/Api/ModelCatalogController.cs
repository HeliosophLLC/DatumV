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
    // startup and caches in a Valtio proxy. Re-fetched on app focus / pull-
    // to-refresh patterns later; for v1 a manual reload covers updates.
    [HttpGet]
    public CatalogManifest GetManifest() => store.Manifest;

    // GET /api/model-catalog/tasks — the engine-defined task vocabulary
    // (name + family + one-line description), one entry per
    // <see cref="TaskTypeRegistry"/> contract. Front-end fetches once at
    // startup to drive the faceted task-filter chips grouped by family.
    // Mirrors the contents of the `system.task_contracts` virtual table over
    // HTTP so the model browser doesn't have to issue SQL.
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

    // GET /api/model-catalog/family-cards/{family} — raw markdown body of
    // the model-family card. Returns 404 when no entry in the family
    // declares a familyCardFile.
    [HttpGet("family-cards/{family}")]
    [Produces("text/markdown")]
    public ActionResult<string> GetFamilyCard(string family)
    {
        string? text = store.GetFamilyCardMarkdown(family);
        if (text is null) return NotFound();
        return Content(text, "text/markdown");
    }

    // GET /api/model-catalog/family-cards/{family}/assets/{**path} —
    // serves screenshots / diagrams / etc. referenced from the family
    // card markdown. The path is resolved against the manifest's
    // `cards/<family>/` subtree with a traversal guard; anything that
    // escapes 404s. Content type is inferred from the file extension —
    // PhysicalFile lets ASP.NET handle range/etag automatically.
    [HttpGet("family-cards/{family}/assets/{**path}")]
    public ActionResult GetFamilyCardAsset(string family, string path)
    {
        string? abs = store.ResolveFamilyCardAssetPath(family, path);
        if (abs is null) return NotFound();
        return PhysicalFile(abs, ContentTypeForFile(abs));
    }

    // GET /api/model-catalog/models/{id}/hero — serves the entry's hero
    // image. 404 when the entry didn't declare HeroImageFile or the
    // file isn't present on disk.
    [HttpGet("models/{id}/hero")]
    public ActionResult GetHeroImage(string id)
    {
        string? abs = store.ResolveHeroImagePath(id);
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

    // GET /api/model-catalog/models/{id}/state — fast probe of local-vs-remote.
    [HttpGet("models/{id}/state")]
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
    // install state for every model in the manifest. Used by the Models
    // view to render install-state badges without N round-trips.
    [HttpGet("states")]
    public async Task<IReadOnlyDictionary<string, ModelInstallState>> GetStates(CancellationToken ct)
        => await downloads.ProbeAllAsync(ct);

    // POST /api/model-catalog/models/{id}/install — kicks off background
    // download. Returns 202 immediately; progress flows over the SignalR
    // hub.
    //   - 404 if id unknown
    //   - 409 if the model is a placeholder (HF repo not yet uploaded)
    //   - 412 if a required license has not been accepted (Precondition Failed)
    //
    // ProducesResponseType attributes are not decorative: NSwag's TS codegen
    // uses them to decide which statuses to treat as success. Without 202
    // explicitly listed, the generated client falls into its "unexpected
    // server error" branch even though the install kicked off successfully.
    [HttpPost("models/{id}/install")]
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

    // DELETE /api/model-catalog/models/{id} — removes the local copy.
    [HttpDelete("models/{id}")]
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

    // POST /api/model-catalog/models/{id}/install/{version} — kicks off a
    // pinned background install for a specific catalog version. Downloads
    // into <id>/<version>/ and registers identifiers under the suffixed
    // <bare>@<digits> form; does NOT flip the active pointer. Returns 202
    // immediately; progress flows over the same SignalR hub channel as
    // bare installs (the model id is the channel key — the chip groups
    // multi-version installs of the same entry into one row).
    [HttpPost("models/{id}/install/{version}")]
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

    // POST /api/model-catalog/models/{id}/activate/{version} — flips the
    // active pointer to a non-active on-disk version. Runs the catalog
    // substrate's version-switch: drops the outgoing version's bare
    // identifiers, re-registers them in pinned form (best-effort, so
    // existing call sites for `models.<bare>@<digits-of-outgoing>` keep
    // working), runs the incoming version's installSql in bare mode,
    // cross-checks identifiers, and flips <id>/active. Synchronous
    // (returns 204 on success); the work is bounded by installSql
    // parse + register, not by I/O.
    [HttpPost("models/{id}/activate/{version}")]
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

    // DELETE /api/model-catalog/models/{id}/versions/{version} — removes
    // a single version's folder + drops the identifiers it registered.
    // Active-version delete also clears the <id>/active pointer (no
    // auto-flip to a sibling). Idempotent for missing versions.
    [HttpDelete("models/{id}/versions/{version}")]
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

    // GET /api/model-catalog/on-disk-versions — bulk view of which
    // version folders exist for each manifest entry. Used by the model
    // card's "Previous versions" disclosure to decide which versions get
    // an Install button (not on disk) vs Activate/Delete (on disk). Map
    // values are the on-disk version folder names; entries with no
    // <id>/ directory are omitted.
    [HttpGet("on-disk-versions")]
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetOnDiskVersions()
    {
        Dictionary<string, IReadOnlyList<string>> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (CatalogModel model in store.Manifest.Models)
        {
            string idRoot = Path.Combine(paths.ModelsRoot, model.Id);
            if (!Directory.Exists(idRoot)) continue;

            // Filter by the declared version set so we only surface
            // folders that match a catalog entry — stray directories
            // from old installs don't pollute the UI list.
            HashSet<string> declared = new(StringComparer.Ordinal);
            foreach (CatalogVersion v in model.Versions) declared.Add(v.Version);

            List<string> onDisk = [];
            foreach (string dir in Directory.EnumerateDirectories(idRoot))
            {
                string name = Path.GetFileName(dir);
                if (declared.Contains(name)) onDisk.Add(name);
            }
            if (onDisk.Count > 0) result[model.Id] = onDisk;
        }
        return result;
    }

    // GET /api/model-catalog/active-versions — one entry per installed
    // model, mapping catalog id → the version string written into
    // <DATUMV_MODELS>/<id>/active. Entries without an active pointer
    // (never installed, fully uninstalled) are omitted; the front-end
    // treats absence as "not installed" and skips drift comparison.
    // Drift detection lives client-side: compare against
    // models[i].versions[0].version from the manifest.
    [HttpGet("active-versions")]
    public IReadOnlyDictionary<string, string> GetActiveVersions()
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (CatalogModel model in store.Manifest.Models)
        {
            string? active = paths.GetActiveVersion(model.Id);
            if (active is not null) result[model.Id] = active;
        }
        return result;
    }

    // GET /api/model-catalog/partial-bytes — bulk view of partial download
    // state. Returns one entry per model with non-zero .part bytes; models
    // with no partials are omitted (UI treats absence as zero). Used by
    // the Models view to surface Resume / Restart affordances.
    [HttpGet("partial-bytes")]
    public async Task<IReadOnlyDictionary<string, long>> GetAllPartialBytes(CancellationToken ct)
        => await downloads.GetAllPartialBytesAsync(ct);

    // DELETE /api/model-catalog/models/{id}/partials — wipes any .part
    // files for the model so a subsequent install starts from byte zero.
    [HttpDelete("models/{id}/partials")]
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
