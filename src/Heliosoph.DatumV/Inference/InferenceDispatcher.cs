using Microsoft.Extensions.Logging;

namespace Heliosoph.DatumV.Inference;

/// <summary>
/// Default <see cref="IInferenceDispatcher"/> implementation. Takes a list
/// of registered backends and a per-call <see cref="BundleManifest"/> +
/// <see cref="InferencePreferences"/>, picks one (backend, device) target
/// for the whole bundle, and loads every session on that target.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Single target per bundle.</strong> Multi-session bundles
/// (Florence-2 with four ONNX files; SD with text-encoder + UNet + VAE)
/// run all sessions on the same backend + device. Mixing devices within
/// a bundle would force constant host↔device transfers on every cross-
/// session call — the savings from putting one session on a faster
/// device get eaten by the data movement.
/// </para>
/// <para>
/// <strong>Selection order.</strong>
/// <list type="number">
///   <item><description>
///     If <see cref="InferencePreferences.ForcedBackend"/> is set, only
///     that backend is considered. Throws if it's not registered.
///   </description></item>
///   <item><description>
///     Otherwise, if the bundle declares <see cref="BundleManifest.PreferredBackends"/>,
///     walk that list in order. Skip backends that aren't registered or
///     fail <see cref="IInferenceBackend.Inspect"/>.
///   </description></item>
///   <item><description>
///     Otherwise, walk all registered backends in registration order.
///   </description></item>
///   <item><description>
///     For each candidate backend, pick a device from
///     <see cref="IInferenceBackend.AvailableDevices"/> via the
///     power-preference ranking (or honour
///     <see cref="InferencePreferences.ForcedDevice"/>).
///   </description></item>
///   <item><description>
///     First (backend, device) pair that produces a successful load
///     for every session in the bundle wins. If a session fails
///     mid-load, the dispatcher disposes the sessions already loaded
///     and propagates the exception — partial bundle loads never
///     leak.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// <strong>No session caching.</strong> Each <see cref="LoadBundleAsync"/>
/// call constructs fresh sessions. Reuse is the residency manager's
/// concern — it intercepts disposal to keep warm sessions alive and
/// hands them back on subsequent loads. That integration lands when the
/// SQL <c>CREATE MODEL</c> runtime hooks into the dispatcher.
/// </para>
/// </remarks>
public sealed class InferenceDispatcher : IInferenceDispatcher
{
    private readonly IReadOnlyList<IInferenceBackend> _backends;
    private readonly ILogger<InferenceDispatcher> _logger;

    /// <summary>
    /// Constructs the dispatcher with the set of backends it should
    /// arbitrate between. Order matters for the default-priority
    /// fallback: the first registered backend is tried first when the
    /// bundle has no preference list.
    /// </summary>
    public InferenceDispatcher(
        IEnumerable<IInferenceBackend> backends,
        ILogger<InferenceDispatcher> logger)
    {
        _backends = backends.ToArray();
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<IInferenceBackend> Backends => _backends;

    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<string, IModelSession>> LoadBundleAsync(
        BundleManifest bundle,
        InferencePreferences preferences,
        CancellationToken cancellationToken)
    {
        ValidateBundle(bundle);

        (IInferenceBackend backend, InferenceDevice device) = PickTarget(bundle, preferences);
        _logger.LogInformation(
            "Dispatching bundle {Bundle} to {Backend} on {Device} ({SessionCount} sessions)",
            bundle.BundleId, backend.Id, device, bundle.Sessions.Count);

        Dictionary<string, IModelSession> sessions = new(
            bundle.Sessions.Count, StringComparer.Ordinal);
        try
        {
            foreach ((string sessionName, string filePath) in bundle.Sessions)
            {
                cancellationToken.ThrowIfCancellationRequested();

                long? declaredBytes = null;
                if (bundle.DeclaredResidentBytes is not null &&
                    bundle.DeclaredResidentBytes.TryGetValue(sessionName, out long bytes))
                {
                    declaredBytes = bytes;
                }

                InferenceLoadRequest request = new(
                    ModelFilePath: filePath,
                    SessionName: sessionName,
                    Device: device,
                    Optimization: InferenceOptimization.Standard,
                    DeclaredResidentBytes: declaredBytes);

                IModelSession session = await backend.LoadAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                sessions[sessionName] = session;
            }

            return sessions;
        }
        catch
        {
            // Any failure (partial load, OOM, file missing) gets the
            // already-loaded sessions disposed. Swallowing disposal
            // exceptions in this path is deliberate — we're already
            // propagating the real error.
            foreach (IModelSession partial in sessions.Values)
            {
                try { partial.Dispose(); }
                catch (Exception disposeEx)
                {
                    _logger.LogWarning(disposeEx,
                        "Failed to dispose partial session during cleanup of bundle {Bundle}",
                        bundle.BundleId);
                }
            }
            throw;
        }
    }

    private static void ValidateBundle(BundleManifest bundle)
    {
        if (bundle.Sessions.Count == 0)
        {
            throw new ArgumentException(
                $"Bundle '{bundle.BundleId}' declares no sessions. " +
                "At least one session entry is required.",
                nameof(bundle));
        }

        foreach ((string sessionName, string filePath) in bundle.Sessions)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException(
                    $"Bundle '{bundle.BundleId}' session '{sessionName}' references " +
                    $"missing file: {filePath}",
                    filePath);
            }
        }
    }

    private (IInferenceBackend Backend, InferenceDevice Device) PickTarget(
        BundleManifest bundle, InferencePreferences preferences)
    {
        IEnumerable<IInferenceBackend> candidates = GetBackendCandidates(bundle, preferences);

        // Track rejections so the final error message tells the caller
        // exactly which backends were tried and why each declined.
        List<string> rejections = new();

        foreach (IInferenceBackend backend in candidates)
        {
            BackendCompatibility compat = backend.Inspect(bundle);
            if (!compat.IsSupported)
            {
                rejections.Add($"{backend.Id}: {compat.UnsupportedReason ?? "no reason given"}");
                continue;
            }

            InferenceDevice? device = PickDevice(backend, preferences);
            if (device is null)
            {
                rejections.Add(
                    $"{backend.Id}: no device matches preferences " +
                    $"(available: [{string.Join(", ", backend.AvailableDevices)}])");
                continue;
            }

            return (backend, device.Value);
        }

        throw new InvalidOperationException(
            $"No registered backend can serve bundle '{bundle.BundleId}'.\n" +
            $"Preferences: ForcedBackend={preferences.ForcedBackend?.ToString() ?? "(none)"}, " +
            $"ForcedDevice={preferences.ForcedDevice?.ToString() ?? "(none)"}, " +
            $"Power={preferences.Power}, Latency={preferences.Latency}\n" +
            $"Bundle preferred backends: [{string.Join(", ", bundle.PreferredBackends)}]\n" +
            "Tried:\n  " + string.Join("\n  ", rejections));
    }

    private IEnumerable<IInferenceBackend> GetBackendCandidates(
        BundleManifest bundle, InferencePreferences preferences)
    {
        // ForcedBackend short-circuits everything else.
        if (preferences.ForcedBackend is InferenceBackendId forced)
        {
            IInferenceBackend? match = _backends.FirstOrDefault(b => b.Id == forced);
            if (match is null)
            {
                throw new InvalidOperationException(
                    $"InferencePreferences.ForcedBackend = {forced} is not registered. " +
                    $"Available backends: [{string.Join(", ", _backends.Select(b => b.Id))}]");
            }
            return new[] { match };
        }

        // Bundle's preferred-backends list, intersected with registered backends.
        // Order matters — the bundle author's ranking determines fallback order.
        if (bundle.PreferredBackends.Count > 0)
        {
            return bundle.PreferredBackends
                .Select(id => _backends.FirstOrDefault(b => b.Id == id))
                .Where(b => b is not null)
                .Select(b => b!);
        }

        // No preference: try registered backends in registration order.
        return _backends;
    }

    private static InferenceDevice? PickDevice(
        IInferenceBackend backend, InferencePreferences preferences)
    {
        IReadOnlyList<InferenceDevice> available = backend.AvailableDevices;
        if (available.Count == 0) return null;

        // ForcedDevice short-circuits power-preference ranking.
        if (preferences.ForcedDevice is InferenceDevice forced)
        {
            return available.Contains(forced) ? forced : null;
        }

        InferenceDevice[] ranking = preferences.Power switch
        {
            PowerPreference.HighPerformance => HighPerformanceRanking,
            PowerPreference.LowPower        => LowPowerRanking,
            _                               => BalancedRanking,
        };

        foreach (InferenceDevice device in ranking)
        {
            if (available.Contains(device)) return device;
        }

        // Available devices that aren't in any ranking shouldn't happen
        // in practice (every InferenceDevice is in at least one ranking),
        // but if it does, fall back to whatever the backend reports first.
        return available[0];
    }

    // Device rankings per power preference. Edits here are policy
    // decisions — when a new InferenceDevice value is added, add it to
    // every ranking that's relevant (most cases: HighPerformance for
    // throughput, LowPower for efficiency, Balanced as the default
    // user expectation).

    /// <summary>
    /// Highest-throughput-first. Dedicated AI accelerators first, then
    /// general-purpose GPU, then CPU as final fallback.
    /// </summary>
    private static readonly InferenceDevice[] HighPerformanceRanking =
    {
        InferenceDevice.OnnxRuntimeCuda,      // NVIDIA dGPU — top of the heap
        InferenceDevice.LlamaSharpCuda,       // LlamaSharp on NVIDIA dGPU
        InferenceDevice.LlamaSharpVulkan,     // LlamaSharp on cross-vendor GPU (AMD/Intel; NVIDIA fallback in standard variant)
        InferenceDevice.OnnxRuntimeDirectMl,  // AMD/Intel dGPU on Windows
        InferenceDevice.OpenVinoGpu,          // Intel iGPU (or Arc dGPU)
        InferenceDevice.OnnxRuntimeCoreMl,    // Apple ANE/GPU via CoreML
        InferenceDevice.OpenVinoNpu,          // Intel NPU — fast but quantised models only
        InferenceDevice.OnnxRuntimeCpu,
        InferenceDevice.OpenVinoCpu,
        InferenceDevice.LlamaSharpCpu,
    };

    /// <summary>
    /// Lowest-power-first. Sustained inference accelerators (NPU) first,
    /// then iGPU, then CPU. Discrete GPUs deprioritised — they idle hot.
    /// </summary>
    private static readonly InferenceDevice[] LowPowerRanking =
    {
        InferenceDevice.OpenVinoNpu,
        InferenceDevice.OpenVinoGpu,
        InferenceDevice.OnnxRuntimeCoreMl,    // Apple ANE is among the most efficient accelerators
        InferenceDevice.OnnxRuntimeCpu,
        InferenceDevice.OpenVinoCpu,
        InferenceDevice.LlamaSharpCpu,
        InferenceDevice.OnnxRuntimeDirectMl,
        InferenceDevice.LlamaSharpVulkan,     // Cross-vendor dGPU — same power category as DirectML
        InferenceDevice.OnnxRuntimeCuda,
        InferenceDevice.LlamaSharpCuda,
    };

    /// <summary>
    /// Default. Mixes throughput and efficiency: prefer dedicated
    /// accelerators when present, but pick the efficient one when
    /// multiple are available.
    /// </summary>
    private static readonly InferenceDevice[] BalancedRanking =
    {
        InferenceDevice.OnnxRuntimeCuda,
        InferenceDevice.LlamaSharpCuda,
        // Vulkan sits just below CUDA. On NVIDIA in the standard variant
        // it's ~85-95% of CUDA for LLM workloads; on AMD/Intel it's the
        // only path that hits the GPU at all. Either way it must rank
        // above every CPU entry so the AMD-RX-580 / Intel-Arc / older-
        // NVIDIA paths pick GPU instead of falling through to CPU.
        InferenceDevice.LlamaSharpVulkan,
        InferenceDevice.OpenVinoNpu,
        InferenceDevice.OnnxRuntimeDirectMl,
        InferenceDevice.OpenVinoGpu,
        InferenceDevice.OnnxRuntimeCoreMl,
        InferenceDevice.OnnxRuntimeCpu,
        InferenceDevice.OpenVinoCpu,
        InferenceDevice.LlamaSharpCpu,
    };
}
