namespace DatumIngest.Models;

/// <summary>
/// Observer interface for <see cref="ModelResidencyManager"/> lifecycle
/// events. Hosts that want to surface residency state to a UI (a status-
/// bar chip, an admin dashboard, a TUI panel) implement this and
/// register via <see cref="ModelCatalog.AddLifecycleObserver"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Calling convention.</strong> Methods are invoked synchronously
/// from inside the residency manager's lock, so implementations MUST
/// return quickly and MUST NOT block on I/O or grab locks of their own.
/// The SignalR-backed implementation in <c>DatumIngest.Web</c> queues
/// the dispatch on the thread pool — that's the canonical shape.
/// </para>
/// <para>
/// <strong>Multiple observers.</strong> The catalog supports more than
/// one observer. Calls fan out in registration order; an observer that
/// throws is logged and skipped — a misbehaving subscriber must not
/// take down the dispatch path.
/// </para>
/// </remarks>
public interface IModelLifecycleObserver
{
    /// <summary>
    /// Fires after a model has been loaded into the residency manager
    /// and its weight cost has been measured. <paramref name="vramUsedBytes"/>
    /// is the manager's running total after this admission — useful for
    /// "we now hold X GB across N models" surfaces.
    /// </summary>
    void OnLoaded(string modelName, long weightCostBytes, long vramUsedBytes);

    /// <summary>
    /// Fires after a resident model has been removed. <paramref name="reason"/>
    /// disambiguates the three eviction paths so UIs can color/badge them
    /// differently (e.g. user-driven vs. LRU vs. calibration's clean-room
    /// sweep).
    /// </summary>
    void OnEvicted(string modelName, long bytes, EvictionReason reason);

    /// <summary>
    /// Fires when a model's active-lease count transitions across the
    /// busy/idle boundary — refs 0→1 (became busy) or N→0 (became idle).
    /// Coalesced this way so the observer stream doesn't fire per
    /// dispatch, which would flood the SignalR channel on multi-query
    /// workloads.
    /// </summary>
    void OnActiveChanged(string modelName, int activeRefs);
}

/// <summary>
/// Why a model was evicted. Lets the UI surface user-driven evictions
/// (EVICT MODEL DDL) distinctly from automatic ones (LRU under VRAM
/// pressure, or calibration's clean-room sweep between ramp steps).
/// </summary>
public enum EvictionReason
{
    /// <summary>
    /// Synchronous tear-down triggered by <c>DROP MODEL</c> or
    /// <c>CREATE OR REPLACE MODEL</c> — disposes the underlying
    /// <see cref="IModel"/> regardless of active refs.
    /// </summary>
    Explicit,

    /// <summary>
    /// User asked for it via <c>EVICT MODEL</c>. Safe-eviction path —
    /// only fires when refs were 0 at the time of the request.
    /// </summary>
    UserRequested,

    /// <summary>
    /// LRU eviction inside the admission path — a new model needed VRAM
    /// the manager couldn't free without dropping an existing one.
    /// </summary>
    Lru,

    /// <summary>
    /// Calibration coordinator forced a fresh load by evicting the
    /// target between ramp steps. Routine; UI should de-emphasise
    /// (no badge, no toast) since it's transient.
    /// </summary>
    Calibration,
}
