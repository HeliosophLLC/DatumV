using DatumIngest.Catalog;
using DatumIngest.Web.Migrations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DatumIngest.Web.Hosting;

// Runs embedded SQL migrations against the local TableCatalog on app
// startup. Registered only when WebHostOptions.ManageLocalCatalog is true;
// SaaS hosts skip this and provision per-principal catalogs elsewhere.
//
// IHostedService.StartAsync is awaited sequentially before the host
// considers itself "started." Kestrel does begin listening before this
// completes (the server itself is a hosted service), but first-run
// migrations apply in well under a second and the SPA load time eclipses
// that window — no explicit readiness gate is needed yet. When migrations
// can take measurable time (data backfills), add a /api/ready probe and a
// "preparing…" state in the SPA.
internal sealed class CatalogInitializationService : IHostedService
{
    private readonly TableCatalog _catalog;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CatalogInitializationService> _logger;

    public CatalogInitializationService(TableCatalog catalog, ILoggerFactory loggerFactory)
    {
        _catalog = catalog;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<CatalogInitializationService>();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Initialising local catalog.");
        MigrationRunner runner = new(_catalog, _loggerFactory.CreateLogger<MigrationRunner>());
        await runner.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
