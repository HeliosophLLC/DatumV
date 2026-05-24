using System.Collections.Concurrent;
using System.Diagnostics;

using Heliosoph.DatumV.Diagnostics;

namespace Heliosoph.DatumV.Models.Calibration;

/// <summary>
/// Orchestrates per-model calibration ramps. Ensures concurrent queries
/// that touch an uncalibrated model see a single ramp pass (deduplicated
/// per model name) and that ramps for different models execute serially
/// (no cross-model VRAM contamination).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why serial.</strong> A calibration ramp measures the model's
/// per-batch VRAM cost in isolation: load → dispatch at batch=1 → record →
/// dispatch at batch=2 → record → … etc. If two models calibrated
/// concurrently, each one's <c>vramAfter</c> reading would include the
/// other model's resident weights + activations, contaminating the curve
/// and making the persisted data unreusable in any other configuration.
/// The <see cref="SemaphoreSlim"/> gate enforces "only one ramp in flight
/// at a time" globally.
/// </para>
/// <para>
/// <strong>Why per-model dedup.</strong> Two queries hitting the same
/// uncalibrated model shouldn't both queue ramp jobs and run them
/// sequentially — the first ramp's result satisfies both. The pending-task
/// map (<see cref="_pending"/>) gives concurrent callers a shared
/// <see cref="Task"/> to <c>await</c>; only the first caller actually
/// starts the ramp.
/// </para>
/// <para>
/// <strong>Dispatch abstraction.</strong> The coordinator doesn't know
/// how to run inference itself — it takes a <see cref="Func{Int32, Task}"/>
/// from the caller that knows. PR9's column-major operator passes a
/// closure over its own dispatch path; tests pass a fake. Decouples
/// calibration orchestration from any single dispatch implementation.
/// </para>
/// <para>
/// <strong>Cancellation.</strong> Each <see cref="EnsureCalibratedAsync"/>
/// caller observes its own <see cref="CancellationToken"/>. Cancellation
/// affects only that caller's wait — the calibration itself runs to
/// completion (or to spill-detection breakage). Other queries waiting on
/// the same ramp keep waiting; the result they'll eventually receive is
/// still useful for future dispatches.
/// </para>
/// </remarks>
public sealed class CalibrationCoordinator
{
    /// <summary>
    /// Doubling batch-size ramp explored during calibration. Stops when
    /// duration-jump spill detection fires or when the ceiling is hit.
    /// Mirrors the dormant <c>DoublingBatchSizePolicy</c>'s steps so the
    /// curve covers the same sizes the eventual <c>CurvePolicy</c> will
    /// consider.
    /// </summary>
    public static readonly IReadOnlyList<int> DefaultRampBatchSizes = [1, 2, 4, 8, 16, 32];

    /// <summary>
    /// Multiplier above which a dispatch's per-row time is considered a
    /// VRAM-spill signal — same threshold the doubling policy used. The
    /// ramp halts on spill; the offending entry and everything above it
    /// are dropped via <see cref="ModelCalibration.RecordSpill"/>.
    /// </summary>
    public const double SpillDetectionMultiplier = 2.0;

    /// <summary>
    /// Default minimum total dispatch duration (milliseconds) before spill
    /// detection is consulted. Below this, sub-millisecond jitter from
    /// scheduling, GC pauses, and Stopwatch resolution dominates the
    /// per-row reading — a 200μs dispatch can legitimately follow a 50μs
    /// dispatch without anything having gone wrong, and that 4× jump
    /// would falsely trip the 2× spill multiplier. Real ONNX dispatches
    /// are tens or hundreds of milliseconds even at batch=1; this
    /// threshold only suppresses detection in test / synthetic harnesses.
    /// </summary>
    public const double DefaultMinDispatchMsForSpillDetection = 5.0;

    /// <summary>
    /// Per-instance spill-detection threshold (milliseconds). Defaults to
    /// <see cref="DefaultMinDispatchMsForSpillDetection"/>. Tests with
    /// deliberately fast dispatch delegates can raise this (e.g. to
    /// <see cref="double.PositiveInfinity"/>) to suppress spill detection
    /// entirely — under heavy parallel test load even modest scheduling
    /// jitter can produce a 2× msPerRow swing across ramp steps, halting
    /// the ramp before <c>MarkCalibrated</c> and leaving the calibration
    /// <see cref="ModelCalibration.State.Stale"/>.
    /// </summary>
    internal double MinDispatchMsForSpillDetection { get; set; } =
        DefaultMinDispatchMsForSpillDetection;

    private readonly ModelCatalog _catalog;
    // Lazy<Task> rather than Task directly: ConcurrentDictionary.GetOrAdd's
    // factory can race-execute multiple times under contention. Storing
    // Task.Run(...) directly in the factory would schedule wasted work
    // (only one Task survives in the dict, but the losers still ran).
    // Lazy<Task> with ExecutionAndPublication mode guarantees the factory
    // runs at most once across all callers — even race-losing Lazies that
    // never make it into the dict have their .Value never accessed and
    // get GC'd without scheduling anything.
    private readonly ConcurrentDictionary<string, Lazy<Task>> _pending =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _calibrationGate = new(1, 1);

    /// <summary>
    /// Constructs a coordinator bound to <paramref name="catalog"/> — used
    /// for catalog entry lookup, file fingerprinting, and (eventually) the
    /// residency / acquire path. Held by reference; entries added to the
    /// catalog after construction are visible to subsequent calibrations.
    /// </summary>
    public CalibrationCoordinator(ModelCatalog catalog)
    {
        _catalog = catalog;
    }

    /// <summary>
    /// Ensures the model has a calibrated curve. Fast-paths when the
    /// model is already calibrated; otherwise queues (or joins) a ramp
    /// pass and awaits its completion.
    /// </summary>
    /// <param name="modelName">
    /// Catalog name (unqualified; no <c>models.</c> prefix). Must be
    /// registered with <see cref="ModelCatalog"/>.
    /// </param>
    /// <param name="dispatch">
    /// Caller-supplied dispatch delegate. Takes the batch size to run
    /// at, performs the dispatch, returns when it's done. The coordinator
    /// brackets the call with VRAM and duration probes so the dispatch
    /// itself can stay focused on inference.
    /// </param>
    /// <param name="cancellationToken">
    /// Honoured for the calling query's wait only — does not cancel an
    /// in-flight ramp that other queries are awaiting.
    /// </param>
    /// <param name="discoverMaxBatchSize">
    /// Optional probe invoked after each ramp step's dispatch to ask the
    /// model what its declared maximum batch size is. Returning a non-null
    /// value &lt;= the current ramp batch halts the ramp immediately and
    /// marks the calibration complete — used to short-circuit fixed-batch
    /// ONNX sessions whose dim[0] is a concrete constant, so the ramp
    /// doesn't burn evict-reload cycles on steps the session can't honour.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// No catalog entry exists for <paramref name="modelName"/>, or the
    /// entry has no <see cref="ModelCatalogEntry.RelativePath"/> (synthetic
    /// backends can't be calibrated since they have no file fingerprint).
    /// </exception>
    public Task EnsureCalibratedAsync(
        string modelName,
        Func<int, Task> dispatch,
        CancellationToken cancellationToken,
        Func<int?>? discoverMaxBatchSize = null)
    {
        // Fast path: already calibrated. No lock, no allocation.
        ModelCalibration? existing = _catalog.CalibrationRegistry.Get(modelName);
        if (existing is { Status: ModelCalibration.State.Calibrated })
        {
            return Task.CompletedTask;
        }

        // Slow path: dedup + queue via Lazy<Task>. See _pending's docstring
        // for why Lazy rather than Task directly.
        Lazy<Task> ramp = _pending.GetOrAdd(modelName, name =>
            new Lazy<Task>(
                // Detach from any specific caller's CancellationToken —
                // see class remarks. Wraps in Task.Run so the actual ramp
                // work happens off the caller's continuation chain.
                () => Task.Run(() => RunCalibrationAsync(name, dispatch, discoverMaxBatchSize)),
                LazyThreadSafetyMode.ExecutionAndPublication));

        return WaitWithCancellationAsync(ramp.Value, cancellationToken);
    }

    private static async Task WaitWithCancellationAsync(Task ramp, CancellationToken ct)
    {
        // Task.WaitAsync respects the per-call cancellation token without
        // affecting the underlying ramp.
        await ramp.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task RunCalibrationAsync(
        string modelName,
        Func<int, Task> dispatch,
        Func<int?>? discoverMaxBatchSize)
    {
        // Serial gate: only one ramp anywhere in flight. Cleared in
        // finally so a thrown ramp doesn't block future calibrations.
        await _calibrationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ModelCatalogEntry? entry = _catalog.TryGetEntry(modelName);
            if (entry is null)
            {
                throw new InvalidOperationException(
                    $"Calibration requested for '{modelName}' but no catalog entry exists. " +
                    "Register the model before triggering calibration.");
            }

            // Resolve the fingerprintable absolute path. Builtins set
            // RelativePath (id-prefixed, relative to the models root);
            // SQL-defined models set FingerprintPath to the descriptor's
            // already-absolute ResolvedUsingPath. Synthetic backends
            // (EchoModel) have neither and can't be calibrated.
            string? absolutePath = entry.FingerprintPath
                ?? (string.IsNullOrEmpty(entry.RelativePath)
                    ? null
                    : Path.GetFullPath(_catalog.PathResolver.ResolveIdPrefixedPath(entry.RelativePath)));
            if (absolutePath is null)
            {
                throw new InvalidOperationException(
                    $"Calibration requested for '{modelName}' but it has no fingerprintable " +
                    "path (neither RelativePath nor FingerprintPath set). Synthetic backends " +
                    "(e.g. EchoModel) cannot be calibrated.");
            }

            string? fingerprint = ModelFileFingerprint.TryCompute(absolutePath);
            if (fingerprint is null)
            {
                throw new InvalidOperationException(
                    $"Calibration requested for '{modelName}' but fingerprint computation failed " +
                    $"for '{absolutePath}'. Verify the file exists and is readable.");
            }

            ModelCalibration calibration = _catalog.CalibrationRegistry.GetOrCreate(modelName, fingerprint);

            using Activity? span = DatumActivity.Calibration.StartActivity($"ramp '{modelName}'");
            DatumActivity.Calibration.Trace($"ramp-start '{modelName}' fingerprint={fingerprint}");
            _catalog.NotifyRampStarted(modelName, fingerprint);

            // Track the last batch size we attempted so a dispatch throw
            // can report it through OnRampHalted. Zero means we threw
            // before measuring anything.
            int lastAttemptedBatch = 0;
            try
            {
                // Clean-room measurement: evict every other unpinned model
                // before the ramp so weight_cost and per-batch total VRAM
                // numbers reflect ONLY this model's footprint. Without this
                // step, calibration measurements bake in whatever happened
                // to be co-resident at ramp time — making the curve
                // unreusable in other configurations and inflating the
                // weight_cost estimate. Pinned models (those a concurrent
                // query is actively dispatching) stay put; the ramp's
                // measurements will be noisier in that case but not wrong.
                EvictOthersForCleanRoom(modelName);

                double bestMsPerRow = 0;
                long lastTotal = 0;

                foreach (int batchSize in DefaultRampBatchSizes)
                {
                    lastAttemptedBatch = batchSize;
                    // Evict the target model itself between ramp steps so
                    // the next dispatch forces ORT to load fresh and grow
                    // its CUDA arena to fit THIS batch's activations. Without
                    // this, the arena allocated for batch=N memoises and the
                    // dispatch at batch=2N sees zero net change in NVML
                    // (the pool already covers it). Recorded totals come
                    // out wrong — usually as 0 at every batch above the
                    // first — and PickLargestFitting then can't distinguish
                    // "small batch" from "the arena was already that big."
                    // The eviction cost (~5-10s reload per step for large
                    // models) is paid once per (host, model, ORT version)
                    // and persisted; the alternative is recalibrating from
                    // scratch every process restart with broken data.
                    //
                    // Pinned models (concurrent query holds a lease) can't
                    // be evicted; in that rare case the dispatch runs
                    // against the existing arena and the measurement is
                    // tainted but still an upper bound — the policy stays
                    // safe, just possibly conservative.
                    ModelResidencyManager.EvictResult evictOutcome =
                        _catalog.ResidencyManager.TryEvictUnpinned(
                            modelName, EvictionReason.Calibration);
                    if (evictOutcome == ModelResidencyManager.EvictResult.Pinned)
                    {
                        DatumActivity.Calibration.Trace(
                            $"ramp-cant-evict '{modelName}' batch={batchSize} — "
                            + "pinned by concurrent lease; measurement may be tainted");
                    }

                    bool measured = TryMeasureBefore(out long vramBefore);

                    // Track the PEAK observed VRAM during this dispatch. We
                    // record peak as the absolute total (weights +
                    // activations) rather than peak - vramBefore. With the
                    // eviction above, the model loads fresh during the
                    // dispatch delegate, so peak reflects exactly what this
                    // batch size needs on a clean device.
                    long peak = vramBefore;
                    using CancellationTokenSource samplerCts = new();
                    Task sampler = measured
                        ? Diagnostics.VramPeakSampler.StartAsync(
                            samplerCts.Token,
                            observed => peak = Math.Max(peak, observed))
                        : Task.CompletedTask;

                    long startTimestamp = Stopwatch.GetTimestamp();
                    try
                    {
                        await dispatch(batchSize).ConfigureAwait(false);
                    }
                    finally
                    {
                        samplerCts.Cancel();
                        try { await sampler.ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                    }
                    double dispatchMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

                    long total = measured ? peak : 0;
                    calibration.Record(batchSize, total, DateTimeOffset.UtcNow);

                    DatumActivity.Calibration.Trace(
                        $"ramp-step '{modelName}' batch={batchSize} total-peak={total}B dispatchMs={dispatchMs:F1}");
                    _catalog.NotifyRampStep(modelName, batchSize, total, dispatchMs);

                    // Declared max-batch cap: the model knows it can't go
                    // higher than this (typically an ONNX session with a
                    // concrete leading input dim). The first dispatch loads
                    // any lazy sessions, so the probe returns a meaningful
                    // value from step 1 onward for procedural models. Halt
                    // the ramp here instead of running redundant steps that
                    // all measure the same internal-batch dispatch.
                    if (discoverMaxBatchSize?.Invoke() is int declaredMax
                        && batchSize >= declaredMax)
                    {
                        DatumActivity.Calibration.Trace(
                            $"ramp-cap '{modelName}' batch={batchSize} hit declared max={declaredMax}");
                        break;
                    }

                    // Spill detection by duration: if per-row time jumped
                    // non-linearly past 2× the best observed, the cliff is
                    // between the prior step and this one. Drop the offending
                    // entry and everything above (RecordSpill does both),
                    // mark Stale, exit the ramp. Below the
                    // MinDispatchMsForSpillDetection threshold, sub-ms jitter
                    // dominates and we skip the check — keeps test / synthetic
                    // harnesses from tripping it spuriously.
                    double currentMsPerRow = batchSize > 0 ? dispatchMs / batchSize : 0;
                    if (bestMsPerRow > 0
                        && dispatchMs >= MinDispatchMsForSpillDetection
                        && currentMsPerRow > bestMsPerRow * SpillDetectionMultiplier)
                    {
                        DatumActivity.Calibration.Trace(
                            $"ramp-spill '{modelName}' batch={batchSize} msPerRow={currentMsPerRow:F1} " +
                            $"best={bestMsPerRow:F1} -> halt");
                        calibration.RecordSpill(batchSize);
                        _catalog.NotifyRampHalted(modelName, batchSize, HaltReason.DurationSpill);
                        return;
                    }
                    // Only update best from measurable dispatches. Sub-ms
                    // readings would lock in a noise-dominated floor that
                    // makes every subsequent legitimate dispatch look like a
                    // spill.
                    if (dispatchMs >= MinDispatchMsForSpillDetection
                        && (bestMsPerRow == 0 || currentMsPerRow < bestMsPerRow))
                    {
                        bestMsPerRow = currentMsPerRow;
                    }

                    // ── Look-ahead growth check ──
                    // Before doubling the batch, project the next step's
                    // total VRAM and stop if it won't fit. We project on
                    // activations (total - weight_cost) since the weights
                    // contribute a constant baseline that doesn't grow with
                    // the batch — projecting on totals directly would
                    // converge slowly for small models where weights
                    // dominate.
                    //
                    // For LINEAR models (act ≈ 2× per doubling) project 2×
                    // the last activation; for SUPER-LINEAR models the
                    // observed ratio rules. Floor the multiplier at 2× even
                    // for sub-linear models — a smaller-than-2× growth
                    // might just mean the arena absorbed some of the cost,
                    // so we don't get cocky.
                    // Only project when the dispatch actually allocated
                    // VRAM — i.e. peak > vramBefore. With absolute-totals
                    // semantics, `total` equals `peak`, which can equal
                    // vramBefore on synthetic / test dispatches (no real
                    // GPU work) OR on warm-pool dispatches where the
                    // allocator absorbed the growth. In neither case is
                    // total a meaningful number for projecting next-batch
                    // cost — it's just whatever the device was using when
                    // we sampled. Skipping the halt here keeps the ramp
                    // running through real batches; the per-step records
                    // still log whatever total we read.
                    if (measured && total > vramBefore
                        && Diagnostics.VramProbe.TryGetUsage(out long curUsed, out long curTotal))
                    {
                        RampProjection projection = ProjectNextRampStep(
                            total, lastTotal, calibration.WeightCostBytes, curUsed, curTotal);
                        if (projection.ShouldHalt)
                        {
                            DatumActivity.Calibration.Trace(
                                $"ramp-halt '{modelName}' batch={batchSize} total={total}B "
                                + $"growth-multiplier={projection.GrowthMultiplier:F2}x "
                                + $"projected-next-total={projection.ProjectedNext}B "
                                + $"avail={projection.ProjectedAvailable}B");
                            _catalog.NotifyRampHalted(modelName, batchSize, HaltReason.LookAheadProjection);
                            break;
                        }
                    }

                    lastTotal = total;
                }

                calibration.MarkCalibrated();
                DatumActivity.Calibration.Trace(
                    $"ramp-complete '{modelName}' entries={calibration.Curve.Count}");
                _catalog.NotifyRampCompleted(modelName, calibration.Curve.Count);

                // Flush immediately. A successful ramp is expensive (tens of
                // seconds + several model loads); the 1-minute background
                // timer is too coarse to protect that work against an
                // unclean shutdown that fires before the next tick.
                _catalog.SaveCalibrationNow();
            }
            catch (Exception ex)
            {
                // The dispatch delegate threw (almost always: the query
                // that triggered calibration errored). Without this
                // catch, RunCalibrationAsync would unwind past every
                // observer notification, leaving the UI chip stuck in
                // its "Calibrating" state with no event to clear
                // activeRamp. Fire a halt so observers see the symmetric
                // close, then rethrow so the awaiting query still
                // surfaces the original error.
                DatumActivity.Calibration.Trace(
                    $"ramp-error '{modelName}' batch={lastAttemptedBatch} "
                    + $"{ex.GetType().Name}: {ex.Message}");
                _catalog.NotifyRampHalted(modelName, lastAttemptedBatch, HaltReason.DispatchError);
                throw;
            }
        }
        finally
        {
            // Free the pending slot first so subsequent callers can start
            // fresh, then release the gate so the next pending model can
            // begin its ramp. Order matters: releasing the gate first
            // would briefly allow a re-entry on the same name.
            _pending.TryRemove(modelName, out _);
            _calibrationGate.Release();
        }
    }

    private static bool TryMeasureBefore(out long vramBefore)
    {
        return Diagnostics.VramProbe.TryGetUsage(out vramBefore, out _);
    }

    /// <summary>
    /// Result of <see cref="ProjectNextRampStep"/>. Carries both the halt
    /// decision and the intermediate values so callers (and tests) can
    /// inspect the projection's reasoning.
    /// </summary>
    internal readonly record struct RampProjection(
        bool ShouldHalt,
        double GrowthMultiplier,
        long ProjectedNext,
        long ProjectedAvailable);

    /// <summary>
    /// Pure projection algorithm for the calibration ramp's look-ahead
    /// guard. Given the latest measured total VRAM
    /// (<paramref name="total"/>), the previous step's total
    /// (<paramref name="lastTotal"/>, 0 if unavailable), the model's
    /// weight cost, and the current device usage / total from NVML,
    /// decide whether the next doubling would overrun and what the
    /// projected next total is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// We project on activations (<c>total - weight_cost</c>) rather
    /// than totals directly, because the weight component is constant
    /// across batch sizes — folding it into the growth ratio would
    /// converge slowly for small models where weights dominate. The
    /// projected next total is then weight_cost + projected activation.
    /// </para>
    /// <para>
    /// Growth multiplier: <c>max(2.0, currentAct / lastAct)</c>. Linear
    /// models (ratio &lt; 2) still extrapolate at 2× — a model that fit
    /// batch=N at less than 2× batch=N/2's activation cost might be
    /// near a ceiling regardless, so we don't get cocky.
    /// Super-linear models (quadratic/cubic) extrapolate at their
    /// observed ratio, catching the explosion at the first step it
    /// manifests.
    /// </para>
    /// <para>
    /// Safety margin: 5% of device total — looser than CurvePolicy's
    /// runtime gate (10%) so calibration doesn't refuse to measure
    /// batches the runtime would happily use.
    /// </para>
    /// </remarks>
    internal static RampProjection ProjectNextRampStep(
        long total, long lastTotal, long weightCost, long currentUsed, long currentTotal)
    {
        long currentAct = Math.Max(0, total - weightCost);
        long lastAct = Math.Max(0, lastTotal - weightCost);

        double growthMultiplier = 2.0;
        if (lastAct > 0)
        {
            double observedRatio = (double)currentAct / lastAct;
            if (observedRatio > growthMultiplier) growthMultiplier = observedRatio;
        }

        long projectedNextAct = (long)(currentAct * growthMultiplier);
        long projectedNextTotal = weightCost + projectedNextAct;
        long projectedAvail = currentTotal - currentUsed - (currentTotal / 20);
        return new RampProjection(
            ShouldHalt: projectedNextTotal > projectedAvail,
            GrowthMultiplier: growthMultiplier,
            ProjectedNext: projectedNextTotal,
            ProjectedAvailable: projectedAvail);
    }

    /// <summary>
    /// Evicts every other unpinned resident model before a ramp so the
    /// measurements reflect only the target model's footprint. Pinned
    /// entries are left alone — evicting under a live lease would crash
    /// the holder.
    /// </summary>
    private void EvictOthersForCleanRoom(string keepModelName)
    {
        ModelResidencyManager residency = _catalog.ResidencyManager;
        foreach ((string name, _, _) in residency.Snapshot())
        {
            if (string.Equals(name, keepModelName, StringComparison.OrdinalIgnoreCase)) continue;
            ModelResidencyManager.EvictResult outcome = residency.TryEvictUnpinned(name);
            if (outcome == ModelResidencyManager.EvictResult.Evicted)
            {
                DatumActivity.Calibration.Trace(
                    $"clean-room-evicted '{name}' for-calibration-of '{keepModelName}'");
            }
        }
    }
}
