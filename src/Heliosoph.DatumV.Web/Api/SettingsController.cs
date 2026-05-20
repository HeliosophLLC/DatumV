using Heliosoph.DatumV.Web.Dtos.Settings;
using Heliosoph.DatumV.Web.Settings;
using Microsoft.AspNetCore.Mvc;

namespace Heliosoph.DatumV.Web.Api;

[ApiController]
[Route("api/settings")]
public sealed class SettingsController(ISettingsService settings) : ControllerBase
{
    [HttpGet]
    public Task<SettingsDto> Get(CancellationToken ct) => settings.GetAsync(ct);

    [HttpPatch]
    public Task<SettingsDto> Patch([FromBody] SettingsPatchDto patch, CancellationToken ct) =>
        settings.PatchAsync(patch, ct);
}
