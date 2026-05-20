using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Providers;
using Heliosoph.DatumV.DatasetLibrary;
using Heliosoph.DatumV.Pooling;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Heliosoph.DatumV.Web.Hosting;

// Mounts the DatasetSchemaCatalog into the local TableCatalog at boot
// and wires the DatasetSchemaBinder so the catalog's bound tables stay
// in sync with the install state pulled from disk.
//
// Runs after CatalogInitializationService completes (migrations +
// model rehydrate) because the model substrate doesn't depend on
// datasets, and registering an init order is simpler than introducing
// an explicit cross-service handshake. The host's
// `IHostedService.StartAsync` order matches DI registration order;
// AddDatumVWeb registers both, datasets after models.
internal sealed class DatasetCatalogInitializationService : IHostedService
{
    private readonly TableCatalog _catalog;
    private readonly DatasetSchemaCatalog _datasetCatalog;
    private readonly DatasetSchemaBinder _binder;
    private readonly IDatasetDownloadService _downloads;
    private readonly Pool _pool;
    private readonly ILogger<DatasetCatalogInitializationService> _logger;

    public DatasetCatalogInitializationService(
        TableCatalog catalog,
        DatasetSchemaCatalog datasetCatalog,
        DatasetSchemaBinder binder,
        IDatasetDownloadService downloads,
        Pool pool,
        ILogger<DatasetCatalogInitializationService> logger)
    {
        _catalog = catalog;
        _datasetCatalog = datasetCatalog;
        _binder = binder;
        _downloads = downloads;
        _pool = pool;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (string schema in _binder.DeclaredSchemas)
        {
            _catalog.MountSchemaBackend(schema, _datasetCatalog);
            _logger.LogInformation("Mounted dataset schema '{Schema}' on TableCatalog.", schema);
        }

        // Pre-flight: route `FROM datasets.X` references through the binder
        // so an uninstalled variant triggers the install modal at parse
        // time instead of a generic "table not found" planner error.
        _catalog.DatasetPreFlightSource = _binder;

        // Mount `system.datasets` — virtual table listing installed
        // bindings. Scans walk the binder's enumeration live, so a fresh
        // install / uninstall before the next query is reflected without
        // re-registration.
        _catalog.Add(new DatasetsTableProvider(_pool, _binder));

        // Reap orphan ingest staging dirs left behind by crashed installs
        // BEFORE the binder probes. Without this, the next install on the
        // same variant either races a stale staging dir (and the rename
        // fails) or just leaks disk space across boots.
        int sweptCount = await _downloads
            .SweepStagingDirsAsync(cancellationToken)
            .ConfigureAwait(false);
        if (sweptCount > 0)
        {
            _logger.LogInformation(
                "Reaped {Count} orphan ingest staging directories at boot.", sweptCount);
        }

        await _binder.RebuildAsync(cancellationToken).ConfigureAwait(false);

        // Subscribe the binder to every terminal install/uninstall so the
        // bound-tables snapshot tracks reality without polling. At-most-
        // one subscriber by contract; we own this slot.
        _downloads.OnVariantsChanged = ct => _binder.RebuildAsync(ct);

        // Release the variant's mounted provider BEFORE Directory.Delete
        // fires. Without this hook the .datum handle stays open and the
        // recursive delete throws a sharing violation on Windows.
        _downloads.OnVariantUninstalling = variantId => _binder.DropVariantBindings(variantId);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _downloads.OnVariantsChanged = null;
        _downloads.OnVariantUninstalling = null;
        return Task.CompletedTask;
    }
}
