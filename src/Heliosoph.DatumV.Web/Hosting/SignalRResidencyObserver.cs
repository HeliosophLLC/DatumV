using Heliosoph.DatumV.Models;
using Heliosoph.DatumV.Web.Hubs;

using Microsoft.AspNetCore.SignalR;

namespace Heliosoph.DatumV.Web.Hosting;

/// <summary>
/// Bridges <see cref="IModelLifecycleObserver"/> events from the engine to
/// the <see cref="CatalogHub"/> SignalR channel. Observer methods are
/// invoked synchronously from inside the residency manager (sometimes
/// from inside its lock), so the adapter MUST NOT block — every method
/// fire-and-forgets the SignalR send onto a fresh task and never awaits.
/// </summary>
/// <remarks>
/// Single-user desktop today; broadcasts to <c>Clients.All</c>. Future
/// multi-tenant deployments will scope by group instead. Sends are
/// wrapped in try/catch so a transient hub failure (connection drop,
/// serialization glitch) traces and continues — observability is a
/// best-effort surface, not part of the engine's correctness path.
/// </remarks>
internal sealed class SignalRResidencyObserver(
    IHubContext<CatalogHub, ICatalogHubClient> hub,
    ILogger<SignalRResidencyObserver> log) : IModelLifecycleObserver
{
    public void OnLoaded(string modelName, long weightCostBytes, long vramUsedBytes)
        => _ = SendAsync(
            "OnModelLoaded", modelName,
            () => hub.Clients.All.OnModelLoaded(
                new ModelLoadedEvent(modelName, weightCostBytes, vramUsedBytes)));

    public void OnEvicted(string modelName, long bytes, EvictionReason reason)
        => _ = SendAsync(
            "OnModelEvicted", modelName,
            () => hub.Clients.All.OnModelEvicted(
                new ModelEvictedEvent(modelName, bytes, Map(reason))));

    public void OnActiveChanged(string modelName, int activeRefs)
        => _ = SendAsync(
            "OnModelActiveChanged", modelName,
            () => hub.Clients.All.OnModelActiveChanged(
                new ModelActiveChangedEvent(modelName, activeRefs)));

    /// <summary>
    /// Fire-and-forget envelope that catches transport exceptions. The
    /// engine observer surface is sync; we never propagate hub-send
    /// failures back into it. Logged at warning so they surface in
    /// hosting telemetry without polluting query traces.
    /// </summary>
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

    private static ModelEvictionReason Map(EvictionReason reason) => reason switch
    {
        EvictionReason.Explicit => ModelEvictionReason.Explicit,
        EvictionReason.UserRequested => ModelEvictionReason.UserRequested,
        EvictionReason.Lru => ModelEvictionReason.Lru,
        EvictionReason.Calibration => ModelEvictionReason.Calibration,
        _ => ModelEvictionReason.Explicit,
    };
}
