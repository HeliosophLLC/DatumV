using DatumIngest.Web.ModelLibrary;
using Microsoft.AspNetCore.Mvc;

namespace DatumIngest.Web.Api;

[ApiController]
[Route("api/model-catalog")]
public sealed class ModelCatalogController : ControllerBase
{
    private readonly IManifestStore _store;
    private readonly ILicenseAcceptanceService _licenses;
    private readonly IModelDownloadService _downloads;

    public ModelCatalogController(
        IManifestStore store,
        ILicenseAcceptanceService licenses,
        IModelDownloadService downloads)
    {
        _store = store;
        _licenses = licenses;
        _downloads = downloads;
    }

    // GET /api/model-catalog — the full manifest. Front-end fetches once at
    // startup and caches in a Valtio proxy. Re-fetched on app focus / pull-
    // to-refresh patterns later; for v1 a manual reload covers updates.
    [HttpGet]
    public CatalogManifest GetManifest() => _store.Manifest;

    // GET /api/model-catalog/licenses/{id}/text — raw license text for
    // acceptance UI. Returns 404 if id unknown or textFile missing.
    [HttpGet("licenses/{id}/text")]
    [Produces("text/plain")]
    public ActionResult<string> GetLicenseText(string id)
    {
        string? text = _store.GetLicenseText(id);
        if (text is null) return NotFound();
        return Content(text, "text/plain");
    }

    // POST /api/model-catalog/licenses/{id}/accept — records explicit
    // acceptance. Idempotent.
    [HttpPost("licenses/{id}/accept")]
    public async Task<IActionResult> AcceptLicense(string id, CancellationToken ct)
    {
        if (!_store.Manifest.Licenses.ContainsKey(id)) return NotFound();
        await _licenses.AcceptAsync(id, ct);
        return NoContent();
    }

    // GET /api/model-catalog/licenses/accepted — list of accepted license ids.
    [HttpGet("licenses/accepted")]
    public async Task<IReadOnlyList<string>> GetAcceptedLicenses(CancellationToken ct)
        => await _licenses.GetAcceptedAsync(ct);

    // GET /api/model-catalog/models/{id}/state — fast probe of local-vs-remote.
    [HttpGet("models/{id}/state")]
    public async Task<ActionResult<ModelInstallState>> GetState(string id, CancellationToken ct)
    {
        try
        {
            return await _downloads.ProbeAsync(id, ct);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    // POST /api/model-catalog/models/{id}/install — kicks off background
    // download. Returns 202 immediately; progress flows over the SignalR
    // hub.
    //   - 404 if id unknown
    //   - 409 if the model is a placeholder (HF repo not yet uploaded)
    //   - 412 if a required license has not been accepted (Precondition Failed)
    [HttpPost("models/{id}/install")]
    public async Task<IActionResult> Install(string id, CancellationToken ct)
    {
        try
        {
            await _downloads.InstallAsync(id, ct);
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
            await _downloads.UninstallAsync(id, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}
