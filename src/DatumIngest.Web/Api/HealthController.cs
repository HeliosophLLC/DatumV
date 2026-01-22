using Microsoft.AspNetCore.Mvc;

namespace DatumIngest.Web.Api;

[ApiController]
[Route("api/health")]
public sealed class HealthController : ControllerBase
{
    [HttpGet]
    public HealthDto Get() => new(Status: "alive", Version: "0.0.0");
}
