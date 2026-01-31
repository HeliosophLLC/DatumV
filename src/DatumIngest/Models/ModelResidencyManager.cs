namespace DatumIngest.Models;

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
        public required long Bytes { get; init; }
        public DateTimeOffset LastUsed { get; set; }
        public int ActiveRefs { get; set; }
    }

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
    /// once, hold forever" behaviour).
    /// </summary>
    public ModelResidencyManager(
        long vramBudgetBytes = UnlimitedBudget,
        TimeSpan? admissionTimeout = null)
    {
        VramBudgetBytes = vramBudgetBytes;
        AdmissionTimeout = admissionTimeout ?? DefaultAdmissionTimeout;
    }

    /// <summary>
    /// Acquires a lease on the model for <paramref name="entry"/>, loading it
    /// if not currently resident. The returned lease holds an active ref
    /// until disposed; callers MUST dispose it (typically via <c>using</c>)
    /// or the model becomes un-evictable.
    /// </summary>
    /// <param name="entry">Catalog entry describing the model.</param>
    /// <param name="modelDirectory">Catalog's model directory; used to resolve <see cref="ModelCatalogEntry.RelativePath"/> for the loader and the file-size heuristic.</param>
    /// <param name="cancellationToken">Honoured during admission-timeout polling.</param>
    /// <returns>A lease wrapping the loaded model.</returns>
    /// <exception cref="InvalidOperationException">
    /// The model can't fit even after evicting all unpinned entries, and the
    /// admission timeout elapsed before any active ref dropped.
    /// </exception>
    public async Task<ModelLease> AcquireAsync(
        ModelCatalogEntry entry,
        string modelDirectory,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long estimatedBytes = entry.EstimatedVramBytes
            ?? EstimateFromFile(entry, modelDirectory);

        DateTimeOffset deadline = DateTimeOffset.UtcNow + AdmissionTimeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Task<IModel> modelTask;
            TaskCompletionSource<IModel>? loaderTcs = null;

            lock (_lock)
            {
                // Re-check under the lock — a Dispose() that races with our
                // outside-lock check at method entry must not let us register
                // a new Resident into a dead manager.
                ObjectDisposedException.ThrowIf(_disposed, this);

                if (_resident.TryGetValue(entry.Name, out Resident? cached))
                {
                    cached.ActiveRefs++;
                    cached.LastUsed = DateTimeOffset.UtcNow;
                    modelTask = cached.ModelTask;
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
                    _resident[entry.Name] = new Resident
                    {
                        ModelTask = loaderTcs.Task,
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
                // ---- I'm the loader. Load outside the lock — the loader may
                // do I/O and CUDA init and must not stall other acquires. ----
                IModel loadedModel;
                try
                {
                    loadedModel = entry.Loader(new ModelLoadContext(entry, modelDirectory));
                }
                catch (Exception ex)
                {
                    lock (_lock)
                    {
                        _resident.Remove(entry.Name);
                        _vramUsedBytes -= estimatedBytes;
                    }
                    loaderTcs.SetException(ex);
                    _ = loaderTcs.Task.Exception; // observe so it cannot surface as UnobservedTaskException when no follower is waiting
                    throw;
                }

                // Re-check disposal under the lock before publishing the
                // result. If Dispose() ran while we were loading, the snapshot
                // it took saw an incomplete ModelTask and skipped this entry —
                // we own the cleanup of the freshly-loaded model.
                lock (_lock)
                {
                    if (_disposed)
                    {
                        _resident.Remove(entry.Name);
                        (loadedModel as IDisposable)?.Dispose();
                        ObjectDisposedException disposedEx = new(nameof(ModelResidencyManager));
                        loaderTcs.SetException(disposedEx);
                        _ = loaderTcs.Task.Exception;
                        throw disposedEx;
                    }
                    Console.Error.WriteLine(
                        $"[residency] Loaded '{entry.Name}' (~{FormatBytes(estimatedBytes)}); " +
                        $"used {FormatBytes(_vramUsedBytes)}/{FormatBudget()}.");
                }
                loaderTcs.SetResult(loadedModel);
                return new ModelLease(this, entry.Name, loadedModel);
            }

            // ---- I'm a follower. The loader is or has been working on this
            // entry; wait for its result. My ActiveRefs bump is already in
            // place so the entry can't be evicted out from under me. ----
            try
            {
                IModel model = await modelTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                return new ModelLease(this, entry.Name, model);
            }
            catch
            {
                // Loader failed → its catch already removed the entry; my
                // TryGetValue will miss and the decrement is a no-op.
                // Cancellation → entry still exists, decrement my bump so
                // the loader's own ref isn't stranded one above zero.
                lock (_lock)
                {
                    if (_resident.TryGetValue(entry.Name, out Resident? r) && r.ActiveRefs > 0)
                        r.ActiveRefs--;
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
    /// not generally called directly.
    /// </summary>
    internal void Release(string modelName)
    {
        lock (_lock)
        {
            if (_resident.TryGetValue(modelName, out Resident? r))
            {
                if (r.ActiveRefs > 0) r.ActiveRefs--;
                r.LastUsed = DateTimeOffset.UtcNow;
            }
        }
    }

    /// <summary>
    /// Tests whether <paramref name="estimatedBytes"/> fits in the budget,
    /// evicting LRU non-active entries as needed. Caller holds the lock.
    /// Returns <see langword="true"/> if room was made (or budget is
    /// unlimited); <see langword="false"/> if all unpinned entries were
    /// evicted and the load still wouldn't fit.
    /// </summary>
    private bool TryFitNew(long estimatedBytes)
    {
        if (VramBudgetBytes < 0) return true; // unlimited

        long free = VramBudgetBytes - _vramUsedBytes;
        if (free >= estimatedBytes) return true;

        long needed = estimatedBytes - free;
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

            if (needed <= 0) return true;
        }

        return false;
    }

    private static long EstimateFromFile(ModelCatalogEntry entry, string modelDirectory)
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
            string p = Path.Combine(modelDirectory, rel);
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
                $"missing from '{modelDirectory}': {string.Join(", ", missing)}. " +
                $"Re-download from ModelCatalogEntry.SourceUrl ({entry.SourceUrl ?? "<unset>"}) " +
                "or set ModelCatalogEntry.EstimatedVramBytes explicitly to bypass the file-size heuristic.");
        }

        // 1.2× covers activations / scratch / small KV cache headroom. This
        // fudge is insufficient for LLMs whose KV cache scales with context
        // length × batch size — set ModelCatalogEntry.EstimatedVramBytes
        // explicitly for those.
        return (long)(total * 1.2);
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
            _vramUsedBytes = 0;
        }
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
    private bool _released;

    /// <summary>The model the lease holds.</summary>
    public IModel Model { get; }

    internal ModelLease(ModelResidencyManager manager, string modelName, IModel model)
    {
        _manager = manager;
        _modelName = modelName;
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
        _manager.Release(_modelName);
    }
}
