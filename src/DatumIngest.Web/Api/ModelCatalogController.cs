using DatumIngest.ModelLibrary;
using Microsoft.AspNetCore.Mvc;

namespace DatumIngest.Web.Api;

[ApiController]
[Route("api/model-catalog")]
public sealed class ModelCatalogController(
    IManifestStore store,
    ILicenseAcceptanceService licenses,
    IModelDownloadService downloads) : ControllerBase
{
    // GET /api/model-catalog — the full manifest. Front-end fetches once at
    // startup and caches in a Valtio proxy. Re-fetched on app focus / pull-
    // to-refresh patterns later; for v1 a manual reload covers updates.
    [HttpGet]
    public CatalogManifest GetManifest() => store.Manifest;

    // GET /api/model-catalog/licenses/{id}/text — raw license text for
    // acceptance UI. Returns 404 if id unknown or textFile missing.
    [HttpGet("licenses/{id}/text")]
    [Produces("text/plain")]
    public ActionResult<string> GetLicenseText(string id)
    {
        string? text = store.GetLicenseText(id);
        if (text is null) return NotFound();
        return Content(text, "text/plain");
    }

    // POST /api/model-catalog/licenses/{id}/accept — records explicit
    // acceptance. Idempotent.
    [HttpPost("licenses/{id}/accept")]
    public async Task<IActionResult> AcceptLicense(string id, CancellationToken ct)
    {
        if (!store.Manifest.Licenses.ContainsKey(id)) return NotFound();
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
