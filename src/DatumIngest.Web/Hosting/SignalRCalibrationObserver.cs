using DatumIngest.Models.Calibration;
using DatumIngest.Web.Hubs;

using Microsoft.AspNetCore.SignalR;

namespace DatumIngest.Web.Hosting;

/// <summary>
/// Bridges <see cref="ICalibrationObserver"/> events to the
/// <see cref="CatalogHub"/> SignalR channel. Mirrors the
/// <see cref="SignalRResidencyObserver"/> pattern: sync observer surface
/// fans out to fire-and-forget async hub sends, transport failures are
/// caught and logged rather than propagated.
/// </summary>
internal sealed class SignalRCalibrationObserver(
    IHubContext<CatalogHub, ICatalogHubClient> hub,
    ILogger<SignalRCalibrationObserver> log) : ICalibrationObserver
{
    public void OnRampStarted(string modelName, string fingerprint)
        => _ = SendAsync(
            "OnCalibrationRampStarted", modelName,
            () => hub.Clients.All.OnCalibrationRampStarted(
                new CalibrationRampStartedEvent(modelName, fingerprint)));

    public void OnRampStep(string modelName, int batchSize, long totalVramBytes, double dispatchMs)
        => _ = SendAsync(
            "OnCalibrationRampStep", modelName,
            () => hub.Clients.All.OnCalibrationRampStep(
                new CalibrationRampStepEvent(modelName, batchSize, totalVramBytes, dispatchMs)));

    public void OnRampHalted(string modelName, int lastBatchSize, HaltReason reason)
        => _ = SendAsync(
            "OnCalibrationRampHalted", modelName,
            () => hub.Clients.All.OnCalibrationRampHalted(
                new CalibrationRampHaltedEvent(modelName, lastBatchSize, Map(reason))));

    public void OnRampCompleted(string modelName, int entryCount)
        => _ = SendAsync(
            "OnCalibrationRampCompleted", modelName,
            () => hub.Clients.All.OnCalibrationRampCompleted(
                new CalibrationRampCompletedEvent(modelName, entryCount)));

    private async Task SendAsync(string method, string modelName, Func<Task> send)
    {
        try
        {
            await send().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex,
                "SignalR {Method} send failed for model '{Model}'", method, modelName);
        }
    }

    private static CalibrationHaltReason Map(HaltReason reason) => reason switch
    {
        HaltReason.LookAheadProjection => CalibrationHaltReason.LookAheadProjection,
        HaltReason.DurationSpill => CalibrationHaltReason.DurationSpill,
        HaltReason.DispatchError => CalibrationHaltReason.DispatchError,
        _ => CalibrationHaltReason.LookAheadProjection,
    };
}
