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

        // Re-apply every persisted CREATE MODEL. The inference dispatcher
        // and models directory were both wired in the TableCatalog factory
        // (see WebHostExtensions.AddDatumIngestWeb), so by the time this
        // hosted service runs everything ApplyCreateModelAsync needs is
        // in place. Per-entry failures are tolerated; the report's
        // warnings make them visible.
        ModelRehydrationReport report = await _catalog.RehydrateModelsAsync(cancellationToken)
            .ConfigureAwait(false);
        if (report.Loaded > 0 || report.Skipped > 0)
        {
            _logger.LogInformation(
                "Rehydrated {Loaded} SQL-defined model(s); skipped {Skipped}.",
                report.Loaded, report.Skipped);
        }
        foreach (string warning in report.Warnings)
        {
            _logger.LogWarning("{Warning}", warning);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
