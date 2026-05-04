using DatumIngest.ModelLibrary;
using Microsoft.AspNetCore.Mvc;

namespace DatumIngest.Web.Api;

// Read-only window onto the central license registry. The license
// metadata used to ride along on each catalog manifest; with the
// registry centralized, the frontend fetches it from here once and
// caches. Text bodies stay on the per-catalog `/licenses/{id}/text`
// routes since they live next to the catalog's accept endpoints.
[ApiController]
[Route("api/licenses")]
public sealed class LicensesController(ILicenseRegistry registry) : ControllerBase
{
    // GET /api/licenses — the full id→metadata map. Tiny (≤20 entries
    // in v1), so the client fetches once at startup and caches.
    [HttpGet]
    public IReadOnlyDictionary<string, CatalogLicense> GetAll() => registry.All;
}
