using DatumIngest.Web.Dtos;
using DatumIngest.Web.Hosting;
using Microsoft.AspNetCore.Mvc;

namespace DatumIngest.Web.Api;

[ApiController]
[Route("api/health")]
public sealed class HealthController(ICurrentContext context) : ControllerBase
{
    [HttpGet]
    public HealthDto Get() => new(
        Status: "alive",
        Version: "0.0.0",
        UserId: context.User.GetUserId(),
        DisplayName: context.User.GetDisplayName(),
        CatalogPath: context.CatalogPath,
        NodeId: context.Node.Id);
}
