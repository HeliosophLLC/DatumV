using Heliosoph.DatumV.Diagnostics;
using Heliosoph.DatumV.ModelLibrary;
using Heliosoph.DatumV.Models.Calibration;

namespace Heliosoph.DatumV.Models;

/// <summary>
/// Tracks which models are loaded into VRAM, enforces a configurable budget,
/// and evicts least-recently-used non-active entries when admission control
/// would otherwise overflow. Sits inside <see cref="ModelCatalog"/> as the
/// runtime lifecycle layer; <see cref="ModelCatalogEntry"/> records the
/// metadata, the manager owns the actual loaded <see cref="IModel"/>s.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Refcount discipline.</strong> Each acquire returns a
/// <see cref="ModelLease"/>; disposing the lease releases the ref. While a
/// model has any active refs it cannot be evicted. The current scope is
/// per-operator-execution (<c>ModelInvocationOperator</c> acquires at the
/// start of <c>ExecuteAsync</c> and releases when the iterator finalises) —
/// pinning is just long enough for the model's batch dispatch.
/// </para>
/// <para>
/// <strong>Admission control.</strong> When acquiring a non-resident model
/// would exceed <see cref="VramBudgetBytes"/>, the manager evicts LRU
/// non-active entries until the load fits. If even after evicting all
/// unpinned entries the load still wouldn't fit, the acquire blocks for up
/// to <see cref="AdmissionTimeout"/> waiting for active refs to drop, then
/// throws.
/// </para>
/// <para>
/// <strong>Single-process scope.</strong> The budget governs one process.
/// Multiple <c>datum-shell</c>/host processes on the same machine each
/// have their own independent budget; cross-process VRAM coordination is
/// the multi-tenant dispatcher's job (Demo 5+) and out of scope here.
/// </para>
/// </remarks>
public sealed class ModelResidencyManager : IDisposable
{
    /// <summary>Sentinel for "no budget" — load freely, never evict for budget reasons.</summary>
    public const long UnlimitedBudget = -1;

    private static readonly TimeSpan DefaultAdmissionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AdmissionPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly object _lock = new();
    private readonly Dictionary<string, Resident> _resident = new(StringComparer.OrdinalIgnoreCase);

    // Entries removed from _resident by EvictAlways while still leased.
    // The IModel inside stays alive until the last lease drains so an
    // in-flight Session.Run doesn't crash with 0xC0000005. Once
    // ActiveRefs hits zero the entry is disposed and dropped from this
    // list. Lookups go by Generation rather than name — multiple loads
    // of the same identifier can coexist here transiently if DROP/CREATE
    // OR REPLACE churn fires during in-flight queries.
    private readonly List<Resident> _pendingDisposal = [];

    // Monotonically increasing under _lock — stamps each Resident so
    // ModelLease can find its origin entry even after EvictAlways moves
    // it into _pendingDisposal (or after a successor entry takes the
    // same name in _resident).
    private long _nextGeneration;

    private readonly CalibrationRegistry? _calibrationRegistry;

    // Serialises concurrent loader invocations so two different models
    // never initialise in parallel on the GPU. Two parallel loads
    // contaminate the weight-cost measurement (each load sees the
    // other's VRAM contribution in its post-load reading), and ORT/CUDA
    // init under contention occasionally races on allocator state.
    // Followers waiting on the same model don't enter this gate —
    // they await the placeholder TCS instead, so per-model dedup
    // still gives them the right value without contending with
    // unrelated loads.
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    private long _vramUsedBytes;
    private bool _disposed;

    /// <summary>
    /// Per-resident-model bookkeeping. Mutable; protected by
    /// <see cref="_lock"/>. The model is held as a <see cref="Task{IModel}"/>
    /// rather than a bare <see cref="IModel"/> so concurrent acquires of the
    /// same not-yet-loaded entry can share one in-flight load instead of
    /// racing: the loader publishes the placeholder task under the lock,
    /// followers find it on lookup, bump the ref count, and <c>await</c> the
    /// same task. Completed tasks satisfy cache hits without any awaiting.
    /// </summary>
    private sealed class Resident
    {
        public required Task<IModel> ModelTask { get; init; }
        // Stable identity for this load. ModelLease records it at acquire
        // time so Release can find the correct entry across the
        // _resident → _pendingDisposal transition and across same-name
        // successor loads.
        public required long Generation { get; init; }
        // Mutable so post-load reconciliation can replace the heuristic
        // admit-time estimate with the calibration-registry's measured
        // weight cost. See RecordWeightCost.
        public required long Bytes { get; set; }
        public DateTimeOffset LastUsed { get; set; }
        public int ActiveRefs { get; set; }
    }

    /// <summary>
    /// Back-reference to the owning catalog so the residency manager
    /// can route lifecycle events through the catalog's observer
    /// fan-out. Set immediately after construction by
    /// <see cref="ModelCatalog"/>; <see langword="null"/> in tests that
    /// instantiate the manager directly (observer calls become no-ops).
    /// </summary>
    internal ModelCatalog? Catalog { get; set; }

    /// <summary>
    /// Maximum VRAM the manager will hold at any one time, in bytes.
    /// <see cref="UnlimitedBudget"/> disables admission control.
    /// </summary>
    public long VramBudgetBytes { get; }

    /// <summary>
    /// How long an <see cref="AcquireAsync"/> call will wait for refs to drop
    /// when the model can't fit even after evicting all unpinned entries.
    /// </summary>
    public TimeSpan AdmissionTimeout { get; }

    /// <summary>Total bytes currently accounted as resident.</summary>
    public long VramUsedBytes
    {
        get { lock (_lock) return _vramUsedBytes; }
    }

    /// <summary>
    /// Diagnostic snapshot — names of all currently-resident models with
    /// their estimated bytes and active-ref counts. Captured under the lock
    /// so the snapshot is consistent.
    /// </summary>
    public IReadOnlyList<(string Name, long Bytes, int ActiveRefs)> Snapshot()
    {
        lock (_lock)
        {
            return _resident
                .Select(kv => (kv.Key, kv.Value.Bytes, kv.Value.ActiveRefs))
                .ToList();
        }
    }

    /// <summary>
    /// Creates a manager with the given budget and timeout. Default budget is
    /// <see cref="UnlimitedBudget"/> (preserves the pre-residency "load
    /// once, hold forever" behaviour). The optional
    /// <paramref name="calibrationRegistry"/> opts the manager into
    /// recording per-model weight cost (VRAM delta around the loader call)
    /// — when null, calibration is in-memory-only or absent and the load
    /// path skips measurement.
    /// </summary>
    public ModelResidencyManager(
        long vramBudgetBytes = UnlimitedBudget,
        TimeSpan? admissionTimeout = null,
        CalibrationRegistry? calibrationRegistry = null)
    {
        VramBudgetBytes = vramBudgetBytes;
        AdmissionTimeout = admissionTimeout ?? DefaultAdmissionTimeout;
        _calibrationRegistry = calibrationRegistry;
    }

    /// <summary>
    /// Acquires a lease on the model for <paramref name="entry"/>, loading it
    /// if not currently resident. The returned lease holds an active ref
    /// until disposed; callers MUST dispose it (typically via <c>using</c>)
    /// or the model becomes un-evictable.
    /// </summary>
    /// <param name="entry">Catalog entry describing the model.</param>
    /// <param name="modelDirectory">Catalog's model directory; passed through to the loader via <see cref="ModelLoadContext.ModelDirectory"/>.</param>
    /// <param name="pathResolver">Per-model path resolver used by the file-size heuristic and the calibration weight-cost recorder. <see langword="null"/> falls back to a flat-layout resolver rooted at <paramref name="modelDirectory"/>.</param>
    /// <param name="cancellationToken">Honoured during admission-timeout polling.</param>
    /// <returns>A lease wrapping the loaded model.</returns>
    /// <exception cref="InvalidOperationException">
    /// The model can't fit even after evicting all unpinned entries, and the
    /// admission timeout elapsed before any active ref dropped.
    /// </exception>
    public async Task<ModelLease> AcquireAsync(
        ModelCatalogEntry entry,
        string modelDirectory,
        CancellationToken cancellationToken,
        IModelPathResolver? pathResolver = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Resolver should always be supplied by ModelCatalog; null-fallback
        // keeps a couple of older test fixtures that called AcquireAsync
        // directly with just (entry, modelDirectory, ct) working — they get
        // the flat layout, which is what they tested against anyway.
        pathResolver ??= new FlatModelPathResolver(modelDirectory);

        // Prefer the measured weight cost from a prior calibration over
        // any heuristic. After the first successful load on this host,
        // RecordWeightCost populates CalibrationRegistry with the real
        // NVML-measured delta; that's a much better admission gate than
        // the file-size heuristic (which is wildly off for SQL-defined
        // models whose "primary" file is a tiny wrapper for multi-GB
        // ONNX sessions). Falls back to the entry's explicit hint, then
        // to the file-size × 1.2 default.
        long estimatedBytes = TryGetCalibratedWeightCost(entry.Name)
            ?? entry.EstimatedVramBytes
            ?? EstimateFromFile(entry, pathResolver);

        DateTimeOffset deadline = DateTimeOffset.UtcNow + AdmissionTimeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Task<IModel> modelTask;
            TaskCompletionSource<IModel>? loaderTcs = null;
            long generation;

            lock (_lock)
            {
                // Re-check under the lock — a Dispose() that races with our
                // outside-lock check at method entry must not let us register
                // a new Resident into a dead manager.
                ObjectDisposedException.ThrowIf(_disposed, this);

                if (_resident.TryGetValue(entry.Name, out Resident? cached))
                {
                    int newRefs = ++cached.ActiveRefs;
                    cached.LastUsed = DateTimeOffset.UtcNow;
                    modelTask = cached.ModelTask;
                    generation = cached.Generation;
                    DatumActivity.Operators.Trace($"[residency] acquire-hit '{entry.Name}' refs={newRefs}");
                    // Coalesce on the busy edge: only fan out the
                    // 0→1 transition (idle → in-use). Mid-burst N→N+1
                    // increments would flood observers without adding
                    // signal — the UI already knows it's busy.
                    if (newRefs == 1) Catalog?.NotifyModelActiveChanged(entry.Name, newRefs);
                }
                else if (TryFitNew(estimatedBytes))
                {
                    // Publish a placeholder Resident under the lock. Any
                    // concurrent acquire for the same name from this point on
                    // sees us in the cache, bumps the ref count, and awaits the
                    // same TCS — no double-load, no orphaned IModel, no VRAM
                    // double-counting.
                    loaderTcs = new TaskCompletionSource<IModel>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    generation = ++_nextGeneration;
                    _resident[entry.Name] = new Resident
                    {
                        ModelTask = loaderTcs.Task,
                        Generation = generation,
                        Bytes = estimatedBytes,
                        LastUsed = DateTimeOffset.UtcNow,
                        ActiveRefs = 1,
                    };
                    _vramUsedBytes += estimatedBytes;
                    modelTask = loaderTcs.Task;
                }
                else
                {
                    goto WaitAndRetry;
                }
            }

            if (loaderTcs is not null)
            {
                // ---- I'm the loader. Take the load gate so no other
                // model loads in parallel — keeps weight-cost measurement
                // clean and avoids ORT/CUDA allocator races. The loader
                // call itself runs outside the residency `_lock` so
                // unrelated lookups stay responsive. ----
                await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                IModel loadedModel;
                long vramBefore = 0;
                bool vramAvailable = false;
                try
                {
                    // Bracket the loader call with VRAM probe readings so
                    // we can record the model's true weight cost on the
                    // calibration registry. The delta becomes the
                    // per-model baseline that the future curve-aware
                    // policy uses to decide whether a batch fits
                    // ("weight + curve[batch] <= free_vram"). Probe miss
                    // → record 0, the calibration coordinator will fill
                    // the value in later via a dedicated calibration
                    // pass.
                    vramAvailable = VramProbe.TryGetUsage(out vramBefore, out _);

                    // Track PEAK VRAM during the loader. ORT session
                    // construction often spikes transient allocations
                    // (workspace probes, kernel JIT) that release before
                    // a post-load snapshot — capturing the peak via
                    // background polling produces a more accurate weight
                    // cost than the persistent post-load delta.
                    long peakVram = vramBefore;
                    using CancellationTokenSource samplerCts = new();
                    Task sampler = vramAvailable
                        ? VramPeakSampler.StartAsync(
                            samplerCts.Token,
                            observed => peakVram = Math.Max(peakVram, observed))
                        : Task.CompletedTask;

                    try
                    {
                        loadedModel = entry.Loader(new ModelLoadContext(entry, modelDirectory, pathResolver));
                    }
                    catch (Exception ex)
                    {
                        samplerCts.Cancel();
                        try { await sampler.ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                        lock (_lock)
                        {
                            // Common case: our Resident is still in _resident.
                            // Roll back the admission reservation. Generation
                            // check guards against a successor that took our
                            // name after EvictAlways moved us to pending — we
                            // must not remove the successor.
                            if (_resident.TryGetValue(entry.Name, out Resident? r)
                                && r.Generation == generation)
                            {
                                _resident.Remove(entry.Name);
                                _vramUsedBytes -= estimatedBytes;
                            }
                            else if (TryFindPendingIndex(generation, out int pendingIdx))
                            {
                                // EvictAlways moved us mid-load; VRAM
                                // accounting was decremented there, so just
                                // drop the pending slot.
                                _pendingDisposal.RemoveAt(pendingIdx);
                            }
                        }
                        loaderTcs.SetException(ex);
                        _ = loaderTcs.Task.Exception; // observe so it cannot surface as UnobservedTaskException when no follower is waiting
                        throw;
                    }

                    samplerCts.Cancel();
                    try { await sampler.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }

                    RecordWeightCost(entry, pathResolver, vramBefore, peakVram, vramAvailable, generation);
                }
                finally
                {
                    _loadGate.Release();
                }

                // Re-check disposal under the lock before publishing the
                // result. If Dispose() ran while we were loading, the snapshot
                // it took saw an incomplete ModelTask and skipped this entry —
                // we own the cleanup of the freshly-loaded model.
                lock (_lock)
                {
                    if (_disposed)
                    {
                        // Remove ourselves from whichever container holds us;
                        // generation guard avoids touching a same-name successor.
                        if (_resident.TryGetValue(entry.Name, out Resident? r0)
                            && r0.Generation == generation)
                        {
                            _resident.Remove(entry.Name);
                        }
                        else if (TryFindPendingIndex(generation, out int pendingIdx))
                        {
                            _pendingDisposal.RemoveAt(pendingIdx);
                        }
                        (loadedModel as IDisposable)?.Dispose();
                        ObjectDisposedException disposedEx = new(nameof(ModelResidencyManager));
                        loaderTcs.SetException(disposedEx);
                        _ = loaderTcs.Task.Exception;
                        throw disposedEx;
                    }
                    Console.Error.WriteLine(
                        $"[residency] Loaded '{entry.Name}' (~{FormatBytes(estimatedBytes)}); " +
                        $"used {FormatBytes(_vramUsedBytes)}/{FormatBudget()}.");
                    DatumActivity.Operators.Trace(
                        $"[residency] loaded '{entry.Name}' bytes={estimatedBytes} used={_vramUsedBytes}");
                    // RecordWeightCost above may have already reconciled
                    // estimatedBytes with the measured value; read the
                    // resident's current Bytes for the observer payload
                    // so the UI sees the same number system.models does.
                    // Generation guard: if EvictAlways moved us to pending
                    // mid-load, a successor entry may now hold our name —
                    // its Bytes don't represent the model we just loaded.
                    long observedBytes = estimatedBytes;
                    if (_resident.TryGetValue(entry.Name, out Resident? r) && r.Generation == generation)
                        observedBytes = r.Bytes;
                    long observedUsed = _vramUsedBytes;
                    Catalog?.NotifyModelLoaded(entry.Name, observedBytes, observedUsed);
                    // The loader's own bump is the 0→1 transition for
                    // this model — fire the busy edge here too.
                    Catalog?.NotifyModelActiveChanged(entry.Name, 1);
                }
                loaderTcs.SetResult(loadedModel);
                return new ModelLease(this, entry.Name, loadedModel, generation);
            }

            // ---- I'm a follower. The loader is or has been working on this
            // entry; wait for its result. My ActiveRefs bump is already in
            // place so the entry can't be evicted out from under me. ----
            try
            {
                IModel model = await modelTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                return new ModelLease(this, entry.Name, model, generation);
            }
            catch
            {
                // Loader failed → its catch already removed the entry; my
                // TryGetValue will miss and the decrement is a no-op.
                // Cancellation → entry still exists, decrement my bump so
                // the loader's own ref isn't stranded one above zero.
                // Generation guard handles the EvictAlways-during-load case:
                // our bumped Resident may have moved into _pendingDisposal,
                // and a same-name successor may now occupy _resident.
                lock (_lock)
                {
                    if (_resident.TryGetValue(entry.Name, out Resident? r)
                        && r.Generation == generation && r.ActiveRefs > 0)
                    {
                        r.ActiveRefs--;
                    }
                    else if (TryFindPendingIndex(generation, out int pendingIdx))
                    {
                        Resident pending = _pendingDisposal[pendingIdx];
                        if (pending.ActiveRefs > 0) pending.ActiveRefs--;
                        if (pending.ActiveRefs == 0)
                        {
                            _pendingDisposal.RemoveAt(pendingIdx);
                            if (pending.ModelTask.IsCompletedSuccessfully)
                                (pending.ModelTask.Result as IDisposable)?.Dispose();
                        }
                    }
                }
                throw;
            }

        WaitAndRetry:
            if (DateTimeOffset.UtcNow >= deadline)
            {
                lock (_lock)
                {
                    long free = VramBudgetBytes < 0 ? 0 : VramBudgetBytes - _vramUsedBytes;
                    throw new InvalidOperationException(
                        $"Model '{entry.Name}' requires ~{FormatBytes(estimatedBytes)} of VRAM " +
                        $"but only {FormatBytes(free)} is free and all {_resident.Count} resident " +
                        $"model(s) are pinned by active queries. Waited {AdmissionTimeout.TotalSeconds:F1}s. " +
                        "Retry after some queries complete, increase ModelResidencyManager.VramBudgetBytes, " +
                        "or set ModelCatalogEntry.EstimatedVramBytes if the heuristic is overestimating.");
                }
            }

            await Task.Delay(AdmissionPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Releases the lease's ref. Called by <see cref="ModelLease.Dispose"/>;
    /// not generally called directly. The <paramref name="generation"/>
    /// identifies which <c>Resident</c> the lease originated from so
    /// concurrent <see cref="EvictAlways"/> + same-name reload churn
    /// doesn't route the decrement to the wrong entry.
    /// </summary>
    internal void Release(string modelName, long generation)
    {
        int? newRefs = null;
        lock (_lock)
        {
            // Common case: the entry is still in _resident under our name and
            // our generation matches — straight decrement.
            if (_resident.TryGetValue(modelName, out Resident? r) && r.Generation == generation)
            {
                if (r.ActiveRefs > 0) r.ActiveRefs--;
                r.LastUsed = DateTimeOffset.UtcNow;
                newRefs = r.ActiveRefs;
                DatumActivity.Operators.Trace($"[residency] release '{modelName}' refs={r.ActiveRefs}");
            }
            else if (TryFindPendingIndex(generation, out int pendingIdx))
            {
                // Our Resident was moved to _pendingDisposal by EvictAlways
                // while we held the lease. Drain our ref; if we were the
                // last lease holder, dispose the model now — the whole point
                // of the lazy-disposal path.
                Resident pending = _pendingDisposal[pendingIdx];
                if (pending.ActiveRefs > 0) pending.ActiveRefs--;
                DatumActivity.Operators.Trace(
                    $"[residency] release-pending '{modelName}' refs={pending.ActiveRefs}");
                if (pending.ActiveRefs == 0)
                {
                    _pendingDisposal.RemoveAt(pendingIdx);
                    if (pending.ModelTask.IsCompletedSuccessfully)
                        (pending.ModelTask.Result as IDisposable)?.Dispose();
                    DatumActivity.Operators.Trace(
                        $"[residency] dispose-after-drain '{modelName}'");
                }
                // No idle-edge notification: observers already saw the
                // ModelEvicted event when EvictAlways ran. A second
                // active-changed event for the same logical entry would
                // confuse UI listeners.
            }
        }
        // Idle edge: fire only on N→0 transitions. Same coalescing
        // rationale as the acquire-hit busy-edge above. Fan-out runs
        // outside the lock so a slow observer can't block other
        // dispatch threads waiting to acquire.
        if (newRefs == 0) Catalog?.NotifyModelActiveChanged(modelName, 0);
    }

    /// <summary>
    /// Locates the index of a pending-disposal entry by its generation
    /// stamp. Caller holds <see cref="_lock"/>.
    /// </summary>
    private bool TryFindPendingIndex(long generation, out int index)
    {
        for (int i = 0; i < _pendingDisposal.Count; i++)
        {
            if (_pendingDisposal[i].Generation == generation)
            {
                index = i;
                return true;
            }
        }
        index = -1;
        return false;
    }

    /// <summary>
    /// Drops the cached <see cref="IModel"/> for <paramref name="modelName"/>
    /// from the resident set immediately so subsequent acquires fall through
    /// to a fresh load. The underlying <see cref="IModel"/> is disposed
    /// synchronously when no leases are active, or held alive in a side list
    /// and disposed when the last lease drains. Used by <c>DROP MODEL</c>
    /// and <c>CREATE OR REPLACE MODEL</c>: the registrar tears down the
    /// descriptor's bound sessions immediately after this returns, but
    /// in-flight queries already holding a lease keep dispatching against
    /// the now-removed entry until their <see cref="ModelLease"/> disposes —
    /// no <c>Session.Run</c> against a disposed handle, no 0xC0000005.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if an entry was removed from the resident set;
    /// <see langword="false"/> when no model was cached under that name.
    /// </returns>
    /// <remarks>
    /// VRAM accounting and the resident-lookup map are both updated
    /// synchronously: a fresh acquire of the same name immediately after
    /// <see cref="EvictAlways"/> sees an empty slot and full budget, even
    /// if the prior load's IModel is still alive draining leases in the
    /// background.
    /// </remarks>
    public bool EvictAlways(string modelName)
    {
        long evictedBytes;
        lock (_lock)
        {
            if (!_resident.TryGetValue(modelName, out Resident? r)) return false;
            _resident.Remove(modelName);
            _vramUsedBytes -= r.Bytes;
            evictedBytes = r.Bytes;

            if (r.ActiveRefs == 0)
            {
                // No outstanding leases — same semantics as the old eager Evict:
                // dispose now, no side-list bookkeeping needed.
                if (r.ModelTask.IsCompletedSuccessfully)
                    (r.ModelTask.Result as IDisposable)?.Dispose();
                DatumActivity.Operators.Trace(
                    $"[residency] evict-always '{modelName}' bytes={r.Bytes} " +
                    $"disposed=sync used={_vramUsedBytes}");
            }
            else
            {
                // Park the entry until its last lease releases. The lease's
                // generation stamp is what Release uses to find it again
                // here — name lookup wouldn't work because a fresh acquire
                // is free to claim this name immediately.
                _pendingDisposal.Add(r);
                DatumActivity.Operators.Trace(
                    $"[residency] evict-always '{modelName}' bytes={r.Bytes} " +
                    $"refs={r.ActiveRefs} disposed=deferred used={_vramUsedBytes}");
            }
        }
        Catalog?.NotifyModelEvicted(modelName, evictedBytes, EvictionReason.Explicit);
        return true;
    }

    /// <summary>
    /// Outcome of <see cref="TryEvictUnpinned"/>. Distinguishes the three
    /// reasons EVICT MODEL might decline to act so the user-facing error
    /// (or success trace) can be specific.
    /// </summary>
    public enum EvictResult
    {
        /// <summary>Entry was resident and unpinned; it has been evicted.</summary>
        Evicted,

        /// <summary>No entry was resident under that name; nothing to do.</summary>
        NotResident,

        /// <summary>Entry is resident but has active leases; not evicted.</summary>
        Pinned,
    }

    /// <summary>
    /// User-facing variant of <see cref="EvictAlways"/>: refuses to evict
    /// (and refuses to dispose the underlying <see cref="IModel"/>) when
    /// any query holds an active lease. Returns
    /// <see cref="EvictResult.Pinned"/> in that case so the caller can
    /// surface a "model is in use" error rather than silently deferring
    /// the eviction.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="EvictAlways"/> which always removes the
    /// entry from the resident set and parks it for lazy disposal when
    /// leases are still active — the right semantics for the engine-
    /// internal <c>DROP MODEL</c> / <c>CREATE OR REPLACE MODEL</c>
    /// teardown paths, where the registrar is about to dispose the
    /// underlying sessions and a refusal would leave the catalog in an
    /// inconsistent state. The user-typed <c>EVICT MODEL</c> surface
    /// prefers the refuse-on-pin behaviour because it's a hint, not a
    /// teardown.
    /// </remarks>
    public EvictResult TryEvictUnpinned(
        string modelName, EvictionReason reason = EvictionReason.UserRequested)
    {
        long evictedBytes;
        lock (_lock)
        {
            if (!_resident.TryGetValue(modelName, out Resident? r)) return EvictResult.NotResident;
            if (r.ActiveRefs > 0)
            {
                DatumActivity.Operators.Trace(
                    $"[residency] evict-refused-pinned '{modelName}' refs={r.ActiveRefs}");
                return EvictResult.Pinned;
            }
            _resident.Remove(modelName);
            _vramUsedBytes -= r.Bytes;
            evictedBytes = r.Bytes;
            if (r.ModelTask.IsCompletedSuccessfully)
                (r.ModelTask.Result as IDisposable)?.Dispose();
            DatumActivity.Operators.Trace(
                $"[residency] evict-unpinned '{modelName}' bytes={r.Bytes} used={_vramUsedBytes}");
        }
        Catalog?.NotifyModelEvicted(modelName, evictedBytes, reason);
        return EvictResult.Evicted;
    }

    /// <summary>
    /// Tests whether <paramref name="estimatedBytes"/> fits in the budget,
    /// evicting LRU non-active entries as needed. Caller holds the lock.
    /// Returns <see langword="true"/> if room was made (or budget is
    /// unlimited); <see langword="false"/> if all unpinned entries were
    /// evicted and the load still wouldn't fit.
    /// </summary>
    /// <remarks>
    /// Two parallel checks gate admission: the engine's internal budget
    /// (<c>_vramUsedBytes</c> vs <see cref="VramBudgetBytes"/>) and an
    /// NVML safety check (current device-wide usage vs total). Either
    /// triggering forces eviction. The NVML check catches two cases the
    /// internal accounting can miss: (a) external processes (a browser,
    /// another engine instance) consuming VRAM the residency manager
    /// doesn't track, and (b) underestimated entries — anything whose
    /// admit-time heuristic was lower than actual VRAM cost. Both lead to
    /// real spill into shared GPU memory; eviction shouldn't wait for
    /// our bookkeeping to catch up.
    /// </remarks>
    private bool TryFitNew(long estimatedBytes)
    {
        bool budgetCheck = VramBudgetBytes < 0 || (VramBudgetBytes - _vramUsedBytes) >= estimatedBytes;
        bool nvmlCheck = !VramProbe.TryGetUsage(out long deviceUsed, out long deviceTotal)
            || (deviceTotal - deviceUsed - NvmlSafetyMargin(deviceTotal)) >= estimatedBytes;

        if (budgetCheck && nvmlCheck) return true;

        // At least one gate refused. Compute the eviction target as the
        // larger of the two shortfalls, so we free enough VRAM regardless
        // of which check fired.
        long budgetShortfall = budgetCheck ? 0
            : estimatedBytes - Math.Max(0, VramBudgetBytes - _vramUsedBytes);
        long nvmlShortfall = nvmlCheck ? 0
            : estimatedBytes - Math.Max(0, deviceTotal - deviceUsed - NvmlSafetyMargin(deviceTotal));
        long needed = Math.Max(budgetShortfall, nvmlShortfall);
        List<KeyValuePair<string, Resident>> evictionOrder = _resident
            .Where(kv => kv.Value.ActiveRefs == 0)
            .OrderBy(kv => kv.Value.LastUsed)
            .ToList();

        foreach (KeyValuePair<string, Resident> kv in evictionOrder)
        {
            // An unpinned entry must have completed loading — the loader holds
            // its own ref until the lease is disposed, and failures remove the
            // entry entirely — so .Result is safe here. Guarded anyway.
            if (kv.Value.ModelTask.IsCompletedSuccessfully)
                (kv.Value.ModelTask.Result as IDisposable)?.Dispose();
            _resident.Remove(kv.Key);
            _vramUsedBytes -= kv.Value.Bytes;
            needed -= kv.Value.Bytes;

            Console.Error.WriteLine(
                $"[residency] Evicted '{kv.Key}' ({FormatBytes(kv.Value.Bytes)}); " +
                $"used {FormatBytes(_vramUsedBytes)}/{FormatBudget()}.");
            DatumActivity.Operators.Trace(
                $"[residency] evicted '{kv.Key}' bytes={kv.Value.Bytes} used={_vramUsedBytes}");
            // Observer fan-out happens inside _lock here. Safe because
            // Catalog.NotifyModelEvicted snapshots observers under its
            // own lock and fans out without holding it; observers
            // themselves are documented as non-reentrant w.r.t. the
            // residency manager.
            Catalog?.NotifyModelEvicted(kv.Key, kv.Value.Bytes, EvictionReason.Lru);

            if (needed <= 0) return true;
        }

        return false;
    }

    private static long EstimateFromFile(ModelCatalogEntry entry, IModelPathResolver pathResolver)
    {
        // Prefer summing all declared files — multi-file models (SDXL, Florence-2,
        // Whisper, etc.) store their graph in a small .onnx and their weights in a
        // large .onnx_data sibling; reading only RelativePath misses the bulk.
        IReadOnlyList<string> paths =
            entry.Files is { Count: > 0 } ? entry.Files
            : entry.RelativePath is not null ? [entry.RelativePath]
            : [];

        // No files declared at all = a synthetic backend (EchoModel and the like).
        // Legitimately zero — no VRAM, no admission cost.
        if (paths.Count == 0) return 0;

        long total = 0;
        List<string>? missing = null;
        foreach (string rel in paths)
        {
            string p = pathResolver.ResolveIdPrefixedPath(rel);
            if (File.Exists(p))
                total += new FileInfo(p).Length;
            else
                (missing ??= []).Add(rel);
        }

        // Declared-but-missing is a broken install. Refusing to estimate (and
        // therefore refusing to load) is the right failure mode: silently
        // returning 0 turned the budget into a no-op for missing files, which
        // let an arbitrary number of "phantom" loads sail past admission
        // control before the loader itself eventually failed deeper in the
        // stack with a less helpful message.
        if (missing is not null)
        {
            throw new InvalidOperationException(
                $"Model '{entry.Name}' declares {paths.Count} file(s) but {missing.Count} are " +
                $"missing from '{pathResolver.ModelsRoot}': {string.Join(", ", missing)}. " +
                $"Re-download from ModelCatalogEntry.SourceUrl ({entry.SourceUrl ?? "<unset>"}) " +
                "or set ModelCatalogEntry.EstimatedVramBytes explicitly to bypass the file-size heuristic.");
        }

        // 1.2× covers activations / scratch / small KV cache headroom. This
        // fudge is insufficient for LLMs whose KV cache scales with context
        // length × batch size — set ModelCatalogEntry.EstimatedVramBytes
        // explicitly for those.
        return (long)(total * 1.2);
    }

    /// <summary>
    /// VRAM held back from the NVML-based admission gate as a safety
    /// margin. Larger of 1 GiB or 10% of device VRAM — matches the
    /// shape the curve-aware batch-size policy uses so the two layers
    /// agree on what "available" means.
    /// </summary>
    private static long NvmlSafetyMargin(long totalVram)
        => Math.Max(1L * 1024 * 1024 * 1024, totalVram / 10);

    /// <summary>
    /// Returns the calibration registry's <c>WeightCostBytes</c> for
    /// <paramref name="modelName"/> when it's been measured (positive),
    /// or <see langword="null"/> otherwise. Used as the highest-priority
    /// input to admission control's <c>estimatedBytes</c>: measured
    /// numbers beat heuristics every time.
    /// </summary>
    private long? TryGetCalibratedWeightCost(string modelName)
    {
        if (_calibrationRegistry is null) return null;
        Calibration.ModelCalibration? cal = _calibrationRegistry.Get(modelName);
        if (cal is null) return null;
        long weight = cal.WeightCostBytes;
        return weight > 0 ? weight : null;
    }

    /// <summary>
    /// Computes the marginal VRAM delta of the just-completed load and
    /// stores it on the calibration registry as the model's
    /// <c>WeightCostBytes</c>. Called after every successful loader
    /// invocation; safe to call regardless of whether calibration is
    /// wired or the model has a backing file — the no-op cases short-
    /// circuit early.
    /// </summary>
    private void RecordWeightCost(
        ModelCatalogEntry entry,
        IModelPathResolver pathResolver,
        long vramBefore,
        long peakVram,
        bool vramAvailable,
        long generation)
    {
        if (_calibrationRegistry is null) return;

        // Resolve the fingerprintable absolute path. Builtins set
        // RelativePath (id-prefixed, relative to the models root);
        // SQL-defined models set FingerprintPath to the descriptor's
        // already-absolute ResolvedUsingPath. Synthetic backends
        // (EchoModel) have neither and skip recording — no
        // fingerprintable identity means no cross-restart calibration
        // anyway.
        string? absolutePath = entry.FingerprintPath
            ?? (string.IsNullOrEmpty(entry.RelativePath)
                ? null
                : Path.GetFullPath(pathResolver.ResolveIdPrefixedPath(entry.RelativePath)));
        if (absolutePath is null) return;

        string? fingerprint = ModelFileFingerprint.TryCompute(absolutePath);
        if (fingerprint is null)
        {
            DatumActivity.Calibration.Trace(
                $"skip-weight-cost '{entry.Name}' reason=fingerprint-failed path={absolutePath}");
            return;
        }

        long weightCost = 0;
        if (vramAvailable)
        {
            // Use the background sampler's PEAK reading rather than a
            // single post-load snapshot. ORT session init transiently
            // spikes allocations (workspace probes, kernel JIT) that
            // release before a naive post-load probe — the peak is the
            // honest weight footprint for admission control.
            long delta = peakVram - vramBefore;
            weightCost = delta > 0 ? delta : 0;
        }

        ModelCalibration calibration = _calibrationRegistry.GetOrCreate(entry.Name, fingerprint);
        calibration.SetWeightCost(weightCost);
        DatumActivity.Calibration.Trace(
            $"weight-cost '{entry.Name}' bytes={weightCost} fingerprint={fingerprint}");

        // Reconcile the residency accounting: if the measured weight cost
        // differs materially from the heuristic estimate we used at admit
        // time, update Resident.Bytes + _vramUsedBytes to the real value
        // so future admission decisions don't think we have phantom
        // headroom (heuristic too low) or evict prematurely (too high).
        if (weightCost > 0)
        {
            lock (_lock)
            {
                // Generation match: don't reconcile a successor entry that
                // took our name after EvictAlways moved us to pending. The
                // pending entry's Bytes is moot — its model is on its way
                // out, and _vramUsedBytes was already decremented.
                if (_resident.TryGetValue(entry.Name, out Resident? r)
                    && r.Generation == generation
                    && r.Bytes != weightCost)
                {
                    long oldBytes = r.Bytes;
                    long delta = weightCost - oldBytes;
                    r.Bytes = weightCost;
                    _vramUsedBytes += delta;
                    DatumActivity.Calibration.Trace(
                        $"reconciled-resident-bytes '{entry.Name}' old={oldBytes} new={weightCost}");
                }
            }
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        const double GiB = 1024d * 1024d * 1024d;
        const double MiB = 1024d * 1024d;
        if (bytes >= GiB) return $"{bytes / GiB:F2} GiB";
        return $"{bytes / MiB:F1} MiB";
    }

    private string FormatBudget()
    {
        return VramBudgetBytes < 0 ? "unlimited" : FormatBytes(VramBudgetBytes);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (Resident r in _resident.Values)
            {
                // In-flight loads have a not-yet-completed ModelTask; the
                // loader's own post-load lock block re-checks _disposed and
                // disposes the freshly-loaded model itself, so we skip those
                // here and only dispose models that are actually resident.
                if (r.ModelTask.IsCompletedSuccessfully)
                    (r.ModelTask.Result as IDisposable)?.Dispose();
            }
            _resident.Clear();
            // Drain the lazy-disposal side list too. Same in-flight-load
            // caveat as above: pending entries with a not-yet-complete
            // ModelTask are owned by their loader's post-load _disposed
            // check, which scans both containers and removes itself.
            foreach (Resident r in _pendingDisposal)
            {
                if (r.ModelTask.IsCompletedSuccessfully)
                    (r.ModelTask.Result as IDisposable)?.Dispose();
            }
            _pendingDisposal.Clear();
            _vramUsedBytes = 0;
        }
        // Intentionally not disposing _loadGate — SemaphoreSlim only
        // allocates unmanaged resources if WaitHandle is accessed, which
        // we never do. Disposing here would race the loader's
        // `finally { _loadGate.Release(); }` and throw
        // ObjectDisposedException out of the release, short-circuiting
        // the post-load `if (_disposed) dispose-the-loaded-model` cleanup
        // and leaking the freshly-loaded model.
    }
}

/// <summary>
/// A handle to an acquired model. Holds an active ref on the model (preventing
/// eviction) until disposed. Use with <c>using</c> at every acquisition site.
/// </summary>
public sealed class ModelLease : IDisposable
{
    private readonly ModelResidencyManager _manager;
    private readonly string _modelName;
    private readonly long _generation;
    private bool _released;

    /// <summary>The model the lease holds.</summary>
    public IModel Model { get; }

    internal ModelLease(ModelResidencyManager manager, string modelName, IModel model, long generation)
    {
        _manager = manager;
        _modelName = modelName;
        _generation = generation;
        Model = model;
    }

    /// <summary>
    /// Releases the active ref. Idempotent — safe to call twice; subsequent
    /// calls are no-ops.
    /// </summary>
    public void Dispose()
    {
        if (_released) return;
        _released = true;
        _manager.Release(_modelName, _generation);
    }
}
