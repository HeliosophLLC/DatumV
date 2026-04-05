using DatumIngest.Catalog;
using DatumIngest.Models;
using DatumIngest.Models.Calibration;

namespace DatumIngest.Web.Hosting;

/// <summary>
/// Hosted service that attaches the SignalR-backed lifecycle and
/// calibration observers to the local <see cref="TableCatalog"/>'s
/// <see cref="ModelCatalog"/> at startup. Runs once when the host
/// starts; the observers stay attached for the catalog's lifetime.
/// </summary>
/// <remarks>
/// <para>
/// Registered only when <c>WebHostOptions.ManageLocalCatalog</c> is true
/// (single-process / desktop mode). SaaS multi-tenant hosts attach
/// per-principal observers elsewhere, where the SignalR group scoping
/// can route events to the right user.
/// </para>
/// <para>
/// Idempotent within one host instance — observers are appended to
/// the catalog's list, so re-running this service would double-fire
/// every event. Hosted services run once per process; not an issue
/// in practice.
/// </para>
/// </remarks>
internal sealed class ModelObserverRegistrationService(
    TableCatalog catalog,
    IModelLifecycleObserver lifecycleObserver,
    ICalibrationObserver calibrationObserver,
    ILogger<ModelObserverRegistrationService> log) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ModelCatalog? models = catalog.Models;
        if (models is null)
        {
            // No ModelCatalog attached — host built with
            // RegisterBuiltinModels=false. Nothing to observe; silent
            // no-op so the host still starts cleanly.
            log.LogInformation(
                "ModelObserverRegistrationService: no ModelCatalog on TableCatalog; skipping.");
            return Task.CompletedTask;
        }

        models.AddLifecycleObserver(lifecycleObserver);
        models.AddCalibrationObserver(calibrationObserver);
        log.LogInformation(
            "ModelObserverRegistrationService: attached residency + calibration observers.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
