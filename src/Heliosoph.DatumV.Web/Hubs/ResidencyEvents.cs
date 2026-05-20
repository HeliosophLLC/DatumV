using Tapper;

namespace Heliosoph.DatumV.Web.Hubs;

/// <summary>
/// SignalR push payloads for <see cref="Heliosoph.DatumV.Models.IModelLifecycleObserver"/>
/// events. Three event records, one per channel method on the hub — kept
/// separate (rather than one discriminated payload) so the client switch
/// is on the method name rather than a Kind field, matching the typed-
/// receiver shape SignalR + TypedSignalR.Client generate.
/// </summary>
/// <remarks>
/// Tapper transpiles these to TypeScript automatically as part of the
/// ClientApp codegen pipeline; consumers on the frontend import them
/// from the generated types module rather than redefining them by hand.
/// </remarks>
[TranspilationSource]
public sealed record ModelLoadedEvent(
    string ModelName,
    long WeightCostBytes,
    long VramUsedBytes);

/// <summary>
/// Mirror of <see cref="Heliosoph.DatumV.Models.EvictionReason"/>. Re-declared
/// in the web DTO layer (rather than referenced directly) so the engine
/// can change its enum members without dragging every consumer through
/// a transpile bump, and so Tapper doesn't have to reach across the
/// project boundary.
/// </summary>
[TranspilationSource]
public enum ModelEvictionReason
{
    Explicit,
    UserRequested,
    Lru,
    Calibration,
}

[TranspilationSource]
public sealed record ModelEvictedEvent(
    string ModelName,
    long Bytes,
    ModelEvictionReason Reason);

/// <summary>
/// Coalesced 0↔1 transition signal. The engine only fans this out on
/// idle→busy and busy→idle edges; intermediate N→N+1 bumps stay local
/// to the residency manager. <c>ActiveRefs</c> is the post-transition
/// count, so consumers can render either edge as either a count or a
/// boolean (active &gt; 0).
/// </summary>
[TranspilationSource]
public sealed record ModelActiveChangedEvent(
    string ModelName,
    int ActiveRefs);
