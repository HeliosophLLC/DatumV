using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.DatasetLibrary;
using Heliosoph.DatumV.Web.Dtos;
using Heliosoph.DatumV.Web.Hosting;
using Microsoft.AspNetCore.Mvc;

namespace Heliosoph.DatumV.Web.Api;

[ApiController]
[Route("api/health")]
public sealed class HealthController(
    ICurrentContext context,
    IServiceProvider services) : ControllerBase
{
    [HttpGet]
    public HealthDto Get()
    {
        // TableCatalog is only registered when ManageLocalCatalog=true.
        // SaaS hosts won't have it, hence the optional resolve.
        string? modelsDir = services.GetService<TableCatalog>()?.Models?.ModelDirectory;
        // Same optional-resolve story for the dataset library — present
        // on the desktop host, absent in SaaS-mode hosts that don't
        // register it.
        string? datasetsCacheDir = services.GetService<IDatasetPathResolver>()?.DatasetsCacheRoot;
        return new(
            Status: "alive",
            Version: "0.0.0",
            UserId: context.User.GetUserId(),
            DisplayName: context.User.GetDisplayName(),
            CatalogPath: context.CatalogPath,
            NodeId: context.Node.Id,
            ModelsDirectory: modelsDir,
            DatasetsCacheDirectory: datasetsCacheDir);
    }
}
