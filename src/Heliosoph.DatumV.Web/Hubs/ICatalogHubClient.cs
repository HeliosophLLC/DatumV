using TypedSignalR.Client;

namespace Heliosoph.DatumV.Web.Hubs;

// Methods the server invokes on connected clients. Single fan-out method
// carrying a discriminated CatalogChangedEvent — see the DTO file for the
// rationale on lean-vs-thick payloads.
[Receiver]
public interface ICatalogHubClient
{
    Task OnPong(string message);

    Task OnCatalogChanged(CatalogChangedEvent change);

    /// <summary>
    /// Fired when the catalog directory's contents change on disk for any
    /// reason that didn't go through a DDL statement — VS Code save, git
    /// checkout, hand-edit, etc. The payload is intentionally empty;
    /// listeners refetch <c>/api/files</c> rather than try to apply a
    /// per-file delta. Self-triggered events from the app's own writes
    /// are coalesced with the corresponding <see cref="OnCatalogChanged"/>
    /// notification by the client-side debounce.
    /// </summary>
    Task OnFilesChanged();

    // ─── Residency lifecycle (IModelLifecycleObserver fan-out) ───
    Task OnModelLoaded(ModelLoadedEvent ev);
    Task OnModelEvicted(ModelEvictedEvent ev);
    Task OnModelActiveChanged(ModelActiveChangedEvent ev);

    // ─── Calibration ramp lifecycle (ICalibrationObserver fan-out) ───
    Task OnCalibrationRampStarted(CalibrationRampStartedEvent ev);
    Task OnCalibrationRampStep(CalibrationRampStepEvent ev);
    Task OnCalibrationRampHalted(CalibrationRampHaltedEvent ev);
    Task OnCalibrationRampCompleted(CalibrationRampCompletedEvent ev);
}
