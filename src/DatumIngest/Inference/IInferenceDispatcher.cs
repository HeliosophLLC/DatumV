namespace DatumIngest.Inference;

/// <summary>
/// The policy layer between SQL MODEL declarations and the backend
/// implementations. The dispatcher knows which backends exist, what
/// hardware each can address, and how to pick one for a given bundle +
/// user preference combination.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One dispatcher per process.</strong> The dispatcher caches
/// per-bundle session handles in cooperation with the residency manager
/// — held strongly while in use, evictable when memory pressure rises.
/// Multiple dispatchers fighting over the same backends would race on
/// VRAM accounting.
/// </para>
/// <para>
/// <strong>Decision flow.</strong>
/// <list type="number">
///   <item><description>
///     If the user (or bundle author) forced a specific backend or
///     device, try only that combination. Honour an explicit "no" by
///     throwing.
///   </description></item>
///   <item><description>
///     Otherwise walk the bundle's <see cref="BundleManifest.PreferredBackends"/>
///     in order. For each, ask <see cref="IInferenceBackend.Inspect"/>; if
///     supported, pick a device from the backend's
///     <see cref="IInferenceBackend.AvailableDevices"/> per the user's
///     <see cref="InferencePreferences.Power"/> and
///     <see cref="InferencePreferences.Latency"/> bias.
///   </description></item>
///   <item><description>
///     If the bundle has no preferred list, walk all registered backends
///     by default priority (ONNX Runtime first today; will become "best
///     for this hardware tier" once we have more data).
///   </description></item>
///   <item><description>
///     If nothing works, throw with a composite diagnostic listing each
///     backend's <c>UnsupportedReason</c>.
///   </description></item>
/// </list>
/// </para>
/// </remarks>
public interface IInferenceDispatcher
{
    /// <summary>All backends registered with the dispatcher, in registration order.</summary>
    IReadOnlyList<IInferenceBackend> Backends { get; }

    /// <summary>
    /// Load the bundle's sessions. Returns one session per entry in the
    /// bundle's <see cref="BundleManifest.Sessions"/>. The returned
    /// dictionary keys match the session names.
    /// </summary>
    /// <param name="bundle">Bundle to load.</param>
    /// <param name="preferences">User-set preferences; defaults pick "Balanced" everywhere.</param>
    /// <param name="cancellationToken">Honoured between session loads.</param>
    ValueTask<IReadOnlyDictionary<string, IInferenceSession>> LoadBundleAsync(
        BundleManifest bundle,
        InferencePreferences preferences,
        CancellationToken cancellationToken);
}
