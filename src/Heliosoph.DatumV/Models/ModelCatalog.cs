using System.Collections.Concurrent;

using Heliosoph.DatumV.Diagnostics;
using Heliosoph.DatumV.ModelLibrary;
using Heliosoph.DatumV.Models.Calibration;

namespace Heliosoph.DatumV.Models;

/// <summary>
/// Process-scoped registry of <see cref="ModelCatalogEntry"/> records and the
/// <see cref="IModel"/> instances they produce. Lives outside <c>ExecutionContext</c>
/// because models are server-wide resources: a loaded model is amortised across
/// queries, sessions, and tenants. Per-query state (memory budget, query meter,
/// spill arenas) belongs to the context; model residency does not.
/// </summary>
/// <remarks>
/// <para>
/// Lookup is namespaced by the SQL surface: <c>models.mobilenetv2</c> resolves to
/// the entry whose <see cref="ModelCatalogEntry.Name"/> equals <c>"mobilenetv2"</c>.
/// The leading <c>"models."</c> qualifier is stripped by the planner before lookup.
/// </para>
/// <para>
/// The actual load / cache / evict lifecycle lives in
/// <see cref="ModelResidencyManager"/>. The catalog hands out
/// <see cref="ModelLease"/>s via <see cref="AcquireAsync"/>; callers
/// (<c>ModelInvocationOperator</c>) hold the lease for the duration of their
/// model use and dispose it when done. Tests that just want to verify "is the
/// catalog wired correctly?" can use the synchronous
/// <see cref="ResolveLeaseSynchronously"/> helper.
/// </para>
/// </remarks>
public sealed class ModelCatalog : IDisposable
{
    /// <summary>
    /// Default model directory when none is explicitly configured. Resolved in
    /// this order:
    /// <list type="number">
    ///   <item><description>The <c>DATUM_MODELS</c> environment variable, if set.</description></item>
    ///   <item><description>A portable per-user fallback —
    ///     <c>%LOCALAPPDATA%/Heliosoph.DatumV/models</c> on Windows,
    ///     <c>~/.local/share/Heliosoph.DatumV/models</c> on Linux/macOS — via
    ///     <see cref="Environment.SpecialFolder.LocalApplicationData"/>.
    ///   </description></item>
    /// </list>
    /// Production deployments either set the env var or pass an explicit path
    /// to the constructor. Tests rely on the env var being set on developer
    /// machines and self-skip when the model file is absent.
    /// </summary>
    public static string DefaultModelDirectory =>
        Environment.GetEnvironmentVariable("DATUM_MODELS")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Heliosoph.DatumV",
            "models");

    private readonly ConcurrentDictionary<string, ModelCatalogEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Absolute path to the directory holding model files. Resolved at construction;
    /// each entry's <see cref="ModelCatalogEntry.RelativePath"/> is combined with this
    /// at load time.
    /// </summary>
    public string ModelDirectory { get; }

    /// <summary>
    /// Per-model on-disk path resolver. Loaders and the residency manager
    /// consult this instead of composing <see cref="ModelDirectory"/> +
    /// relative segments by hand so the catalog substrate's per-version
    /// folder layout can be introduced by swapping the resolver rather
    /// than rewriting every callsite. Initial implementation is the flat
    /// layout — behaviourally identical to today's <c>Path.Combine</c>
    /// patterns.
    /// </summary>
    public IModelPathResolver PathResolver { get; }

    /// <summary>
    /// The residency manager that owns the loaded <see cref="IModel"/>
    /// instances and enforces the VRAM budget. Created with the catalog;
    /// budget defaults to <see cref="ModelResidencyManager.UnlimitedBudget"/>
    /// — set <see cref="VramBudgetBytes"/> to bound it.
    /// </summary>
    public ModelResidencyManager ResidencyManager { get; }

    /// <summary>
    /// Convenience accessor for the residency manager's VRAM budget. Setting
    /// after construction is not supported in this build — initialise via the
    /// <see cref="ModelCatalog(string?, long, TimeSpan?)"/> ctor when you need
    /// a non-default budget.
    /// </summary>
    public long VramBudgetBytes => ResidencyManager.VramBudgetBytes;

    /// <summary>
    /// Engine-wide batch-size policy consulted by
    /// <see cref="Heliosoph.DatumV.Execution.Operators.ModelInvocationOperator"/>
    /// at each dispatch. Defaults to <see cref="CurvePolicy"/>: uses
    /// per-model calibration curves when available, falls back to
    /// <c>batch=1</c> for uncalibrated models. When the operator
    /// collapses an adjacent MIO chain into one multi-invocation node,
    /// VRAM contention is coordinated at the plan layer (one model
    /// dispatching at a time, lease released between invocations); the
    /// policy only has to worry about single-model headroom checks.
    /// Hosts that want deterministic dispatch counts swap in
    /// <see cref="BatchOnePolicy"/> (tests) or
    /// <see cref="StaticBatchSizePolicy"/> (benchmarks).
    /// </summary>
    public IBatchSizePolicy BatchSizePolicy { get; set; }

    /// <summary>
    /// Per-model VRAM calibration data — populated by the calibration
    /// coordinator on first acquire, consumed by the (future) curve-aware
    /// batch-size policy to pick the largest batch that fits available
    /// VRAM. Persisted across process restarts via the optional
    /// <see cref="CalibrationStore"/> wired in by the host; in-memory only
    /// when no store is configured.
    /// </summary>
    public CalibrationRegistry CalibrationRegistry { get; } = new();

    /// <summary>
    /// Orchestrates per-model calibration ramps. Lazily constructed on
    /// first access; bound to this catalog so it can resolve entries and
    /// compute file fingerprints. The (future) curve-aware policy and the
    /// (future) column-major operator both reach in through here to
    /// trigger or join calibration on demand.
    /// </summary>
    public CalibrationCoordinator CalibrationCoordinator => _calibrationCoordinator.Value;

    private readonly Lazy<CalibrationCoordinator> _calibrationCoordinator;

    /// <summary>
    /// Resolves a user-supplied model path against the host's model directory,
    /// honouring the <c>file://</c> escape for absolute paths. Shared by
    /// <c>CREATE MODEL USING</c> and any introspection surface (e.g.
    /// <c>inference.onnx_inspect</c>) that takes a model path argument.
    /// </summary>
    /// <param name="path">
    /// The user-supplied path. A leading <c>file://</c> marks an absolute
    /// path (anywhere on disk); a rooted absolute path is returned
    /// canonicalised; otherwise the path is treated as relative to the
    /// host's <see cref="ModelDirectory"/> and returned verbatim — what
    /// you see in the SQL is what gets loaded. Authors of catalog
    /// <c>installSql</c> files write the version segment explicitly
    /// (e.g. <c>'sd-turbo/2026-05-29/text_encoder/model.onnx'</c>) and
    /// ad-hoc <c>inference.onnx_inspect</c> calls do the same. No
    /// implicit version-segment injection happens at this layer.
    /// </param>
    /// <param name="models">
    /// The host's model catalog, or <see langword="null"/> when none is wired.
    /// Only consulted for relative paths.
    /// </param>
    /// <param name="callerContext">
    /// Short human-readable label of the caller (e.g. <c>"CREATE MODEL foo"</c>,
    /// <c>"inference.onnx_inspect"</c>) used in the "no ModelCatalog wired"
    /// error so users can tell which surface tripped the check.
    /// </param>
    public static string ResolveFilePath(string path, ModelCatalog? models, string callerContext)
    {
        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return path["file://".Length..];
        }

        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        if (models is null)
        {
            throw new InvalidOperationException(
                $"{callerContext}: '{path}' is a relative path but no ModelCatalog is " +
                "configured on this host. Use a 'file://'-prefixed absolute path, or wire " +
                "TableCatalog.Models before invoking.");
        }

        // Verbatim join under ModelDirectory — no implicit version-segment
        // injection. Catalog SQL files declare literal versioned paths
        // (e.g. 'sd-turbo/2026-05-29/text_encoder/model.onnx'), so the
        // path written by the author is the path on disk. Fixes the
        // segment-doubling bug where ad-hoc inference.onnx_inspect calls
        // with an already-versioned path would get the active version
        // injected a second time.
        return Path.GetFullPath(Path.Combine(models.ModelDirectory, path));
    }

    /// <summary>
    /// Cadence at which the calibration registry is flushed to disk when
    /// a <see cref="CalibrationStore"/> is wired in. Per-tick writes are
    /// cheap (small JSON file), so a one-minute interval keeps the on-disk
    /// state recent without burning IO on no-op writes.
    /// </summary>
    private static readonly TimeSpan CalibrationSaveInterval = TimeSpan.FromMinutes(1);

    private readonly CalibrationStore? _calibrationStore;
    private readonly HostFingerprint? _hostFingerprint;
    private readonly Timer? _calibrationSaveTimer;
    private readonly Lock _calibrationSaveLock = new();

    // Observer fan-out lists. Read-mostly: writes happen at host wiring
    // time (web/CLI host registers a forwarder); reads happen on every
    // residency / calibration event. A copy-on-write list would be
    // marginally lower-allocation but unnecessary at the scale of "a
    // handful of observers, fired at human-perceptible event rates."
    private readonly List<IModelLifecycleObserver> _lifecycleObservers = [];
    private readonly List<ICalibrationObserver> _calibrationObservers = [];
    private readonly Lock _observerLock = new();

    /// <summary>Creates a catalog rooted at <paramref name="modelDirectory"/>.</summary>
    /// <param name="modelDirectory">
    /// Absolute path to the models directory. <see langword="null"/> uses
    /// <see cref="DefaultModelDirectory"/>.
    /// </param>
    public ModelCatalog(string? modelDirectory = null)
        : this(modelDirectory, ModelResidencyManager.UnlimitedBudget, admissionTimeout: null)
    {
    }

    /// <summary>
    /// Creates a catalog with a specific VRAM budget and optional admission
    /// timeout. Use this overload when you want eviction to actually fire —
    /// the parameterless form leaves the budget unlimited (load-and-hold-
    /// forever, the same shape as before residency was introduced).
    /// </summary>
    public ModelCatalog(string? modelDirectory, long vramBudgetBytes, TimeSpan? admissionTimeout)
        : this(modelDirectory, vramBudgetBytes, admissionTimeout, calibrationStore: null, hostFingerprint: null)
    {
    }

    /// <summary>
    /// Full ctor with calibration persistence wired in. When both
    /// <paramref name="calibrationStore"/> and <paramref name="hostFingerprint"/>
    /// are non-null, persisted calibration is rehydrated into
    /// <see cref="CalibrationRegistry"/> at construction and flushed back
    /// on a background timer + on <see cref="Dispose"/>. Either argument
    /// being null disables persistence — the in-memory registry still
    /// works, calibrations just don't survive process restarts.
    /// </summary>
    public ModelCatalog(
        string? modelDirectory,
        long vramBudgetBytes,
        TimeSpan? admissionTimeout,
        CalibrationStore? calibrationStore,
        HostFingerprint? hostFingerprint,
        IModelPathResolver? pathResolver = null,
        Heliosoph.DatumV.ModelLibrary.ICatalogActiveVersionLookup? activeVersionLookup = null)
    {
        ModelDirectory = modelDirectory ?? DefaultModelDirectory;
        PathResolver = pathResolver ?? new VersionedModelPathResolver(
            ModelDirectory,
            activeVersionLookup ?? Heliosoph.DatumV.ModelLibrary.NullCatalogActiveVersionLookup.Instance);
        ResidencyManager = new ModelResidencyManager(vramBudgetBytes, admissionTimeout, CalibrationRegistry);
        ResidencyManager.Catalog = this;
        _calibrationStore = calibrationStore;
        _hostFingerprint = hostFingerprint;
        // Default policy uses the calibration registry on this catalog.
        // Hosts that need a different policy (tests, benchmarks) overwrite
        // BatchSizePolicy after construction.
        BatchSizePolicy = new CurvePolicy(CalibrationRegistry);
        // Lazy so a catalog instance that never triggers calibration
        // (e.g. a tightly-scoped test) doesn't pay for a coordinator's
        // semaphore + dictionary allocation.
        _calibrationCoordinator = new Lazy<CalibrationCoordinator>(
            () => new CalibrationCoordinator(this),
            LazyThreadSafetyMode.ExecutionAndPublication);

        if (_calibrationStore is not null && _hostFingerprint is not null)
        {
            LoadResult result = _calibrationStore.Load(CalibrationRegistry, _hostFingerprint);
            DatumActivity.Calibration.Trace(
                $"load status={result.Status} loaded={result.LoadedCount} path={_calibrationStore.FilePath}");

            // Background save. Period start = interval (not Zero) so we
            // don't race the host's startup-time mutations with a no-op
            // first tick.
            _calibrationSaveTimer = new Timer(
                callback: _ => TrySaveCalibration(),
                state: null,
                dueTime: CalibrationSaveInterval,
                period: CalibrationSaveInterval);
        }
    }

    /// <summary>
    /// Flushes the in-memory calibration registry to <see cref="CalibrationStore"/>
    /// immediately. No-op when persistence isn't wired (no fingerprintable
    /// host or no store). Safe to call from any thread; serialised with
    /// the background timer's writes.
    /// </summary>
    /// <remarks>
    /// Callers: the calibration coordinator after a successful ramp (so a
    /// freshly-measured curve survives an unclean shutdown that misses the
    /// next timer tick), and host-level signal handlers that want a final
    /// flush before exit.
    /// </remarks>
    public void SaveCalibrationNow() => TrySaveCalibration();

    /// <summary>
    /// Registers <paramref name="observer"/> to receive residency
    /// lifecycle events. Observers are notified in registration order;
    /// an observer that throws is swallowed so a misbehaving subscriber
    /// can't take down the dispatch path.
    /// </summary>
    public void AddLifecycleObserver(IModelLifecycleObserver observer)
    {
        lock (_observerLock) _lifecycleObservers.Add(observer);
    }

    /// <summary>
    /// Registers <paramref name="observer"/> to receive calibration ramp
    /// lifecycle events. Same fan-out semantics as
    /// <see cref="AddLifecycleObserver"/>.
    /// </summary>
    public void AddCalibrationObserver(ICalibrationObserver observer)
    {
        lock (_observerLock) _calibrationObservers.Add(observer);
    }

    // Fan-out helpers. Snapshot under the lock, fan out without it so a
    // slow (or recursive) observer can't deadlock the dispatch path.
    // Each call is try/catch-wrapped so a thrown observer doesn't
    // skip later subscribers or propagate up into the engine.
    internal void NotifyModelLoaded(string modelName, long weightCostBytes, long vramUsedBytes)
    {
        IModelLifecycleObserver[] snapshot;
        lock (_observerLock) snapshot = _lifecycleObservers.ToArray();
        foreach (IModelLifecycleObserver obs in snapshot)
        {
            try { obs.OnLoaded(modelName, weightCostBytes, vramUsedBytes); }
            catch (Exception ex)
            {
                DatumActivity.Operators.Trace(
                    $"lifecycle-observer-threw OnLoaded model='{modelName}' "
                    + $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    internal void NotifyModelEvicted(string modelName, long bytes, EvictionReason reason)
    {
        IModelLifecycleObserver[] snapshot;
        lock (_observerLock) snapshot = _lifecycleObservers.ToArray();
        foreach (IModelLifecycleObserver obs in snapshot)
        {
            try { obs.OnEvicted(modelName, bytes, reason); }
            catch (Exception ex)
            {
                DatumActivity.Operators.Trace(
                    $"lifecycle-observer-threw OnEvicted model='{modelName}' "
                    + $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    internal void NotifyModelActiveChanged(string modelName, int activeRefs)
    {
        IModelLifecycleObserver[] snapshot;
        lock (_observerLock) snapshot = _lifecycleObservers.ToArray();
        foreach (IModelLifecycleObserver obs in snapshot)
        {
            try { obs.OnActiveChanged(modelName, activeRefs); }
            catch (Exception ex)
            {
                DatumActivity.Operators.Trace(
                    $"lifecycle-observer-threw OnActiveChanged model='{modelName}' "
                    + $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    internal void NotifyRampStarted(string modelName, string fingerprint)
    {
        ICalibrationObserver[] snapshot;
        lock (_observerLock) snapshot = _calibrationObservers.ToArray();
        foreach (ICalibrationObserver obs in snapshot)
        {
            try { obs.OnRampStarted(modelName, fingerprint); }
            catch (Exception ex)
            {
                DatumActivity.Calibration.Trace(
                    $"calibration-observer-threw OnRampStarted model='{modelName}' "
                    + $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    internal void NotifyRampStep(string modelName, int batchSize, long totalVramBytes, double dispatchMs)
    {
        ICalibrationObserver[] snapshot;
        lock (_observerLock) snapshot = _calibrationObservers.ToArray();
        foreach (ICalibrationObserver obs in snapshot)
        {
            try { obs.OnRampStep(modelName, batchSize, totalVramBytes, dispatchMs); }
            catch (Exception ex)
            {
                DatumActivity.Calibration.Trace(
                    $"calibration-observer-threw OnRampStep model='{modelName}' "
                    + $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    internal void NotifyRampHalted(string modelName, int lastBatchSize, HaltReason reason)
    {
        ICalibrationObserver[] snapshot;
        lock (_observerLock) snapshot = _calibrationObservers.ToArray();
        foreach (ICalibrationObserver obs in snapshot)
        {
            try { obs.OnRampHalted(modelName, lastBatchSize, reason); }
            catch (Exception ex)
            {
                DatumActivity.Calibration.Trace(
                    $"calibration-observer-threw OnRampHalted model='{modelName}' "
                    + $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    internal void NotifyRampCompleted(string modelName, int entryCount)
    {
        ICalibrationObserver[] snapshot;
        lock (_observerLock) snapshot = _calibrationObservers.ToArray();
        foreach (ICalibrationObserver obs in snapshot)
        {
            try { obs.OnRampCompleted(modelName, entryCount); }
            catch (Exception ex)
            {
                DatumActivity.Calibration.Trace(
                    $"calibration-observer-threw OnRampCompleted model='{modelName}' "
                    + $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void TrySaveCalibration()
    {
        if (_calibrationStore is null || _hostFingerprint is null) return;

        // Serialise multiple savers (timer tick + Dispose) so we don't
        // get interleaved JSON writes.
        lock (_calibrationSaveLock)
        {
            try
            {
                int entryCount = CalibrationRegistry.Snapshot().Count;
                _calibrationStore.Save(CalibrationRegistry, _hostFingerprint);
                DatumActivity.Calibration.Trace(
                    $"save ok entries={entryCount} path={_calibrationStore.FilePath}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                // Calibration is advisory; a failed save shouldn't take
                // down the catalog. Trace and continue — the next tick
                // (or shutdown flush) will try again.
                DatumActivity.Calibration.Trace($"save failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Registers <paramref name="entry"/>. Throws when an entry with the same name
    /// is already registered — replacement requires explicit
    /// <see cref="Unregister"/> first to avoid silent shadowing.
    /// </summary>
    public void Register(ModelCatalogEntry entry)
    {
        if (!_entries.TryAdd(entry.Name, entry))
        {
            throw new InvalidOperationException(
                $"Model '{entry.Name}' is already registered. Call Unregister first if replacement is intended.");
        }
    }

    /// <summary>
    /// Removes the entry. Any already-loaded <see cref="IModel"/> instance
    /// stays in the residency manager until it's evicted naturally — this
    /// just removes the registration so future acquires for the same name
    /// fail to resolve.
    /// </summary>
    public bool Unregister(string name)
    {
        return _entries.TryRemove(name, out _);
    }

    /// <summary>
    /// Returns the entry for <paramref name="name"/> if registered.
    /// </summary>
    public ModelCatalogEntry? TryGetEntry(string name)
        => _entries.TryGetValue(name, out ModelCatalogEntry? entry) ? entry : null;

    /// <summary>
    /// All registered entries, keyed by <see cref="ModelCatalogEntry.Name"/>.
    /// Used by the future <c>sys.models</c> virtual table to project catalog
    /// state into SQL.
    /// </summary>
    public IReadOnlyDictionary<string, ModelCatalogEntry> Entries => _entries;

    /// <summary>
    /// Acquires a <see cref="ModelLease"/> for the given model name, loading
    /// the model into VRAM if not already resident. The lease holds an
    /// active ref until disposed; callers must use <c>using</c> (or
    /// equivalent) at the call site so the manager can evict the model
    /// after the work completes.
    /// </summary>
    /// <param name="name">Catalog name (the unqualified model identifier — no <c>models.</c> prefix).</param>
    /// <param name="cancellationToken">Honoured during admission-timeout polling.</param>
    /// <returns>A lease wrapping the loaded model.</returns>
    /// <exception cref="InvalidOperationException">
    /// No entry registered for <paramref name="name"/>, or admission timed out.
    /// </exception>
    public Task<ModelLease> AcquireAsync(string name, CancellationToken cancellationToken)
    {
        ModelCatalogEntry entry = TryGetEntry(name)
            ?? throw new InvalidOperationException(
                $"No model registered as '{name}'. Register it via ModelCatalog.Register before referencing it from SQL.");

        return ResidencyManager.AcquireAsync(entry, ModelDirectory, cancellationToken, PathResolver);
    }

    /// <summary>
    /// Synchronous resolve for tests / setup paths that just need the model
    /// instance and don't care about the residency lifecycle. The returned
    /// lease MUST still be disposed; this is just sugar over
    /// <see cref="AcquireAsync"/>.<see cref="Task{T}.GetAwaiter"/>.<see cref="System.Runtime.CompilerServices.TaskAwaiter{T}.GetResult"/>.
    /// </summary>
    public ModelLease ResolveLeaseSynchronously(string name)
        => AcquireAsync(name, CancellationToken.None).GetAwaiter().GetResult();

    /// <inheritdoc />
    public void Dispose()
    {
        _calibrationSaveTimer?.Dispose();
        TrySaveCalibration();
        ResidencyManager.Dispose();
    }
}
