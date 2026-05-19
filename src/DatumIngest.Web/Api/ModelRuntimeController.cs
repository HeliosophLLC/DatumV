using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Models;
using Heliosoph.DatumV.Models.Calibration;
using Heliosoph.DatumV.Web.Hubs;

using Microsoft.AspNetCore.Mvc;

using Tapper;

namespace Heliosoph.DatumV.Web.Api;

/// <summary>
/// REST surface for runtime model state — currently-resident models,
/// calibration curves, and the EVICT action. Companion to the SignalR
/// push channel on <see cref="CatalogHub"/>: the client seeds its
/// Valtio state from these endpoints on page load and applies hub
/// events on top.
/// </summary>
/// <remarks>
/// Distinct from <c>ModelCatalogController</c> (download / install /
/// uninstall lifecycle) — this controller deals with what's running
/// in VRAM right now, not what's available on disk.
/// </remarks>
[ApiController]
[Route("api/model-runtime")]
public sealed class ModelRuntimeController(TableCatalog catalog) : ControllerBase
{
    /// <summary>
    /// GET /api/model-runtime/residency — currently-resident models +
    /// their byte cost + active-lease count. Empty array when no
    /// <see cref="ModelCatalog"/> is attached (e.g. host built with
    /// RegisterBuiltinModels=false).
    /// </summary>
    [HttpGet("residency")]
    public IReadOnlyList<ResidentModelDto> GetResidency()
    {
        ModelCatalog? models = catalog.Models;
        if (models is null) return [];
        return models.ResidencyManager.Snapshot()
            .Select(r => new ResidentModelDto(r.Name, r.Bytes, r.ActiveRefs))
            .ToList();
    }

    /// <summary>
    /// GET /api/model-runtime/calibration — every model's calibration
    /// curve plus weight cost. The popover on the calibration chip
    /// pulls this once when it opens and applies hub-pushed ramp-step
    /// events incrementally afterward.
    /// </summary>
    [HttpGet("calibration")]
    public IReadOnlyList<CalibrationCurveDto> GetCalibration()
    {
        ModelCatalog? models = catalog.Models;
        if (models is null) return [];
        return models.CalibrationRegistry.Snapshot()
            .Select(kv => new CalibrationCurveDto(
                kv.Key,
                kv.Value.WeightCostBytes,
                kv.Value.Status.ToString().ToLowerInvariant(),
                kv.Value.Curve
                    .OrderBy(e => e.Key)
                    .Select(e => new CalibrationEntryDto(e.Key, e.Value.TotalVramBytes))
                    .ToList()))
            .ToList();
    }

    /// <summary>
    /// POST /api/model-runtime/{name}/evict — user-driven eviction.
    /// Mirrors the SQL <c>EVICT MODEL</c> surface: refuses when the
    /// model has active leases. Returns 200 with the outcome enum so
    /// the UI can render the right toast (evicted / not-resident /
    /// in-use). 404 when the catalog isn't running.
    /// </summary>
    [HttpPost("{name}/evict")]
    public ActionResult<EvictOutcomeDto> Evict(string name)
    {
        ModelCatalog? models = catalog.Models;
        if (models is null) return NotFound();
        ModelResidencyManager.EvictResult result =
            models.ResidencyManager.TryEvictUnpinned(name, EvictionReason.UserRequested);
        return new EvictOutcomeDto(result switch
        {
            ModelResidencyManager.EvictResult.Evicted => EvictStatus.Evicted,
            ModelResidencyManager.EvictResult.NotResident => EvictStatus.NotResident,
            ModelResidencyManager.EvictResult.Pinned => EvictStatus.Pinned,
            _ => EvictStatus.NotResident,
        });
    }
}

/// <summary>
/// One row of <c>system.residency_snapshot</c> as a wire DTO. Mirrors
/// the SQL surface so the UI can read identically-shaped data from
/// either path.
/// </summary>
[TranspilationSource]
public sealed record ResidentModelDto(
    string Name,
    long WeightCostBytes,
    int ActiveRefs);

[TranspilationSource]
public sealed record CalibrationEntryDto(
    int BatchSize,
    long TotalVramBytes);

[TranspilationSource]
public sealed record CalibrationCurveDto(
    string ModelName,
    long WeightCostBytes,
    string Status,
    IReadOnlyList<CalibrationEntryDto> Entries);

[TranspilationSource]
public enum EvictStatus
{
    Evicted,
    NotResident,
    Pinned,
}

[TranspilationSource]
public sealed record EvictOutcomeDto(EvictStatus Status);
