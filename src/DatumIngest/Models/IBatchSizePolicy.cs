using System.Collections.Concurrent;

using DatumIngest.Diagnostics;

namespace DatumIngest.Models;

/// <summary>
/// Test seam over <see cref="VramProbe"/>. Production uses
/// <see cref="NvmlVramProbe"/>; tests inject a fake to exercise the
/// doubling-ramp logic without a real GPU.
/// </summary>
public interface IVramProbe
{
    /// <summary>
    /// Returns the current VRAM usage. Same semantics as
    /// <see cref="VramProbe.TryGetUsage"/>.
    /// </summary>
    bool TryGetUsage(out long usedBytes, out long totalBytes);
}

/// <summary>
/// Production probe — forwards to the static <see cref="VramProbe"/>.
/// </summary>
public sealed class NvmlVramProbe : IVramProbe
{
    /// <summary>Shared singleton.</summary>
    public static readonly NvmlVramProbe Instance = new();

    private NvmlVramProbe() { }

    /// <inheritdoc />
    public bool TryGetUsage(out long usedBytes, out long totalBytes)
        => VramProbe.TryGetUsage(out usedBytes, out totalBytes);
}

/// <summary>
/// Per-dispatch policy that picks the sub-batch size for
/// <see cref="DatumIngest.Execution.Operators.ModelInvocationOperator"/>'s
/// chunked dispatch loop. Replaces the previous static
/// <c>model.PreferredBatchSize ?? rowsThisBatch</c> read. Stateful per
/// model name so a policy can ramp up (or down) batch sizes over a
/// sequence of dispatches based on observed VRAM usage.
/// </summary>
/// <remarks>
/// <para>
/// MIO calls <see cref="ChooseBatchSize"/> at the start of every chunk
/// and <see cref="RecordDispatch"/> after the chunk completes. The
/// policy is free to change its mind between chunks within the same
/// upstream batch — that's how the doubling tuner ramps from 1 → 2 → 4
/// without a separate calibration phase.
/// </para>
/// <para>
/// <strong>No user knob.</strong> Policy choice is engine-wide, not
/// per-query. This keeps SQL scripts portable across machines with
/// different VRAM — the same script gets the right batch size on a
/// 6 GB laptop, a 24 GB workstation, and a remote A100 alike. Tests
/// can swap policies via the host wiring; user-facing SQL has no
/// override surface (and won't until a measured workload demands it).
/// </para>
/// </remarks>
public interface IBatchSizePolicy
{
    /// <summary>
    /// Picks the sub-batch size for the next dispatch of
    /// <paramref name="model"/>. Returns a value in <c>[1, rowsRemaining]</c>.
    /// </summary>
    int ChooseBatchSize(IModel model, int rowsRemaining);

    /// <summary>
    /// Reports the outcome of a dispatch so the policy can update its
    /// state. <paramref name="vramBefore"/> / <paramref name="vramAfter"/>
    /// are <see cref="VramProbe"/> snapshots taken bracketing the
    /// dispatch; pass <c>-1</c> for either when the probe is unavailable.
    /// <paramref name="dispatchMs"/> is wall-clock time spent inside
    /// <c>InferBatchAsync</c> — the doubling policy uses non-linear jumps
    /// in per-row dispatch time to detect VRAM spill (which doesn't show
    /// up in NVML readings on Windows when memory is paged to shared GPU
    /// memory).
    /// </summary>
    void RecordDispatch(IModel model, int batchSize, long vramBefore, long vramAfter, double dispatchMs);
}

/// <summary>
/// Always returns batch=1. Active engine default while batched cross-row
/// model dispatch is shelved pending the column-major + eviction work.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the regression is intentional.</strong> The
/// <see cref="DoublingBatchSizePolicy"/> ramps each model independently to
/// the largest batch its weights+activations fit in. With two large
/// models (depth-anything-v2-small at ~17 GB for batch=32, plus a sibling
/// running concurrently for the remaining ~5 GB), the per-model ramps
/// don't coordinate — each one's idea of "VRAM headroom" is stale by the
/// time the other dispatches, and the residency manager cannot reliably
/// prevent ORT from spilling activations into Windows shared GPU memory.
/// The result is uneven, much-slower execution than serial batch=1
/// dispatch with both models resident.
/// </para>
/// <para>
/// <strong>Resurrection plan.</strong> Restore <c>DoublingBatchSizePolicy</c>
/// as the default once the planner can collapse multi-model plans into a
/// column-major operator that owns its own residency phases (run model A
/// across the batch, evict, run model B). Without that primitive, even a
/// "perfect" per-model batch-size policy can't avoid VRAM contention with
/// concurrent dispatches sharing the same device.
/// </para>
/// </remarks>
public sealed class BatchOnePolicy : IBatchSizePolicy
{
    /// <summary>Shared singleton — the policy is stateless.</summary>
    public static readonly BatchOnePolicy Instance = new();

    private BatchOnePolicy() { }

    /// <inheritdoc />
    public int ChooseBatchSize(IModel model, int rowsRemaining)
        => rowsRemaining <= 0 ? 0 : 1;

    /// <inheritdoc />
    public void RecordDispatch(IModel model, int batchSize, long vramBefore, long vramAfter, double dispatchMs)
    {
        // No state to update — we always return 1.
    }
}

/// <summary>
/// No-op policy that mirrors the pre-policy behaviour:
/// <c>model.PreferredBatchSize ?? rowsRemaining</c>, no per-dispatch
/// tuning. Useful for tests that need predictable dispatch counts and
/// for production hosts that opt out of VRAM-driven sizing.
/// </summary>
public sealed class StaticBatchSizePolicy : IBatchSizePolicy
{
    /// <summary>Shared singleton — the policy is stateless.</summary>
    public static readonly StaticBatchSizePolicy Instance = new();

    private StaticBatchSizePolicy() { }

    /// <inheritdoc />
    public int ChooseBatchSize(IModel model, int rowsRemaining)
    {
        int preferred = model.PreferredBatchSize ?? rowsRemaining;
        if (preferred <= 0) preferred = rowsRemaining;
        return Math.Min(preferred, rowsRemaining);
    }

    /// <inheritdoc />
    public void RecordDispatch(IModel model, int batchSize, long vramBefore, long vramAfter, double dispatchMs)
    {
        // Stateless policy ignores measurements.
    }
}

/// <summary>
/// Probe-driven doubling tuner: starts at batch=1 on first dispatch,
/// measures VRAM delta around each dispatch, doubles the batch size for
/// the next dispatch when the conservative prediction fits in remaining
/// VRAM, and settles once doubling would exceed the budget.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Calibration is the work.</strong> Each ramp step is a real
/// dispatch with real rows — there's no separate calibration phase. The
/// ramp 1 → 2 → 4 → 8 → 16 → 32 covers 63 rows over 6 dispatches; the
/// 7th onwards run at the settled max. For 100 rows that's ~7 dispatches
/// total vs ~25 at static batch=4.
/// </para>
/// <para>
/// <strong>Conservative prediction.</strong> The "should I double?"
/// check uses <c>nextBatchPredictedBytes = nextBatch × lastDeltaPerRow × 1.2</c>
/// — 20% headroom above the linear extrapolation. Leaves some
/// performance on the table at the boundary but never overshoots into
/// the VRAM-spill cliff. If we under-estimate, the next ramp step will
/// catch it (a query with more rows than the current ramp settled at
/// will keep trying to grow).
/// </para>
/// <para>
/// <strong>State survives eviction-and-reload.</strong> Keyed by model
/// name, not <see cref="IModel"/> instance. A model evicted from the
/// residency manager and reloaded later picks up its previous settled
/// batch size on the first call (no re-ramping). Conditions may have
/// changed in the interim (sibling model loaded, another process
/// consumed VRAM), so <see cref="ChooseBatchSize"/> always validates the
/// stored size against the current probe reading and shrinks if it no
/// longer fits.
/// </para>
/// <para>
/// <strong>Fallback when probe unavailable.</strong> Non-NVIDIA hosts
/// and the CPU EP can't sample VRAM. The policy degrades to
/// <see cref="StaticBatchSizePolicy"/>'s answer — same as today.
/// </para>
/// </remarks>
public sealed class DoublingBatchSizePolicy : IBatchSizePolicy
{
    /// <summary>
    /// Hard ceiling when the model doesn't declare its own
    /// <see cref="IModel.PreferredBatchSize"/>. The doubling ramp settles
    /// here even if VRAM headroom would allow more — at very large
    /// batches, ONNX kernel-launch and IO marshalling start to dominate
    /// dispatch cost and per-row throughput plateaus. 256 is the
    /// rule-of-thumb sweet spot for image-heavy models on consumer
    /// hardware; LLM models with KV-cache scaling should override
    /// <see cref="IModel.PreferredBatchSize"/> to a tighter value.
    /// </summary>
    public const int DefaultCeiling = 256;

    /// <summary>
    /// Multiplier above which a dispatch's per-row time is considered a
    /// VRAM-spill signal. Spilling into shared GPU memory typically
    /// inflates per-kernel latency by 10× or more, so 2× is a comfortable
    /// margin above benign variability (cache effects, kernel-launch
    /// jitter, GC pauses) while still catching the cliff before more
    /// dispatches waste seconds each. When breached, the policy halves
    /// the current batch and settles there — the cliff is at most one
    /// doubling away from a known-good value.
    /// </summary>
    public const double SpillDetectionMultiplier = 2.0;

    /// <summary>
    /// Upper bound for blind doubling when the VRAM probe shows no
    /// per-row delta (model already resident + ORT reusing its internal
    /// arena → activations invisible to NVML). At small batches blind
    /// growth is safe — duration-based spill detection catches overshoot
    /// — but we cap the blind phase so we never leap from batch=1 to the
    /// full ceiling without ever measuring something. 32 lands in the
    /// "GPU-saturated for image-sized models" zone the user measured;
    /// past it we'd really want a delta to extrapolate from.
    /// </summary>
    public const int BlindGrowthCeiling = 32;

    private readonly IVramProbe _probe;
    private readonly ConcurrentDictionary<string, ModelState> _state =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Constructs a doubling policy using <paramref name="probe"/> for
    /// VRAM measurements. Defaults to <see cref="NvmlVramProbe.Instance"/>;
    /// tests inject a fake to exercise the ramp logic deterministically.
    /// </summary>
    public DoublingBatchSizePolicy(IVramProbe? probe = null)
    {
        _probe = probe ?? NvmlVramProbe.Instance;
    }

    /// <inheritdoc />
    public int ChooseBatchSize(IModel model, int rowsRemaining)
    {
        if (rowsRemaining <= 0) return 0;

        // Fast fall-back: probe unavailable (non-NVIDIA host, CPU-only
        // build, probe init failed). Behave identically to the static
        // policy — no ramping, no measurement. Without this guard, the
        // policy would settle at batch=1 on every host without a GPU.
        if (!_probe.TryGetUsage(out long used, out long total))
        {
            return StaticBatchSizePolicy.Instance.ChooseBatchSize(model, rowsRemaining);
        }

        ModelState state = _state.GetOrAdd(model.Name, _ => new ModelState());
        lock (state)
        {
            if (state.CurrentBatch == 0)
            {
                // Cold start: first-ever dispatch for this model. Begin
                // at 1 so the very first measurement gives us a clean
                // per-row delta to extrapolate from.
                state.CurrentBatch = 1;
            }

            int ceiling = ResolveCeiling(model);

            // Validate the stored batch still fits given current VRAM
            // conditions. Only shrinks when `used` itself is approaching
            // the device cap — i.e. an external pressure (sibling model
            // loaded, another process consuming VRAM) has eaten into the
            // headroom we proved was safe last time we dispatched.
            //
            // Do NOT extrapolate "used + predicted-batch-cost > total" the
            // way the growth path does. ORT retains its arena allocations
            // between dispatches, so `used` already includes the current
            // batch's footprint; re-adding the predicted cost on top
            // double-counts and cascades the batch size down to 1 even
            // when the just-completed dispatch ran fine.
            long safety = SafetyMargin(total);
            while (state.CurrentBatch > 1 && used + safety > total)
            {
                state.CurrentBatch /= 2;
                state.Settled = true;
                DatumActivity.Operators.Trace(
                    $"batch-size-policy: model={model.Name} pressure-shrink-to={state.CurrentBatch} "
                    + $"used={used}B total={total}B");
            }

            if (state.CurrentBatch > ceiling)
            {
                state.CurrentBatch = ceiling;
                state.Settled = true;
            }

            return Math.Min(state.CurrentBatch, rowsRemaining);
        }
    }

    /// <inheritdoc />
    public void RecordDispatch(IModel model, int batchSize, long vramBefore, long vramAfter, double dispatchMs)
    {
        if (batchSize <= 0) return;

        ModelState state = _state.GetOrAdd(model.Name, _ => new ModelState());
        lock (state)
        {
            // ────────── Spill detection via dispatch duration ──────────
            //
            // The critical signal that VRAM-snapshot before/after misses
            // entirely: when ORT spills activations into Windows' shared
            // GPU memory, dedicated-VRAM readings stay at the cap (NVML
            // reports the device side only) but every kernel waits on
            // PCIe traffic. The dispatch's wall-clock time jumps
            // non-linearly with batch size — that's the cliff.
            //
            // Detection: per-row time grew >2× over the best-seen value.
            // Spill response: halve the batch and settle there. The ramp
            // doesn't get to try going higher again — we know the cliff
            // is at most one doubling away.
            if (dispatchMs > 0 && batchSize > 0)
            {
                double currentMsPerRow = dispatchMs / batchSize;
                if (state.BestMsPerRow > 0
                    && currentMsPerRow > state.BestMsPerRow * SpillDetectionMultiplier
                    && state.CurrentBatch > 1)
                {
                    int shrunk = Math.Max(1, state.CurrentBatch / 2);
                    DatumActivity.Operators.Trace(
                        $"batch-size-policy: model={model.Name} spill-detected "
                        + $"batch={batchSize} msPerRow={currentMsPerRow:F1} "
                        + $"best={state.BestMsPerRow:F1} shrink-to={shrunk}");
                    state.CurrentBatch = shrunk;
                    state.Settled = true;
                    return;
                }
                // Track the best per-row dispatch time. Used as the
                // reference for spill detection on subsequent dispatches.
                // Only updated when we're NOT in spill territory — a
                // spill reading should never lower the bar for what
                // counts as healthy throughput.
                if (state.BestMsPerRow <= 0 || currentMsPerRow < state.BestMsPerRow)
                {
                    state.BestMsPerRow = currentMsPerRow;
                }
            }

            // Probe unavailable or this dispatch's measurements weren't
            // captured (sentinel -1 from MIO): nothing more to update.
            // Duration-based spill detection above still ran. Static
            // fast-fall-back kicks in via ChooseBatchSize.
            if (vramBefore < 0 || vramAfter < 0) return;

            // VRAM delta is "what this dispatch's activations cost on top
            // of the model's already-resident weights." A negative or
            // zero delta means the probe missed the peak (allocations
            // released before the post-snapshot) or the dispatch was
            // tiny; either way, leave the prior per-row estimate alone
            // and let the next dispatch refine it.
            if (vramAfter > vramBefore)
            {
                long delta = vramAfter - vramBefore;
                long perRow = delta / batchSize;
                // Use the latest observation, not a running max. Earlier
                // observations conflate one-time costs (the model-load
                // delta on the first batch=1 dispatch in particular) with
                // per-row marginal cost. Once a bigger batch has run, its
                // per-row reading is cleaner — model already resident, ORT
                // arena warmed — and locking in the pessimistic cold-start
                // number stalls the ramp prematurely (batch=16 won't grow
                // to 32 because predicted = 32 × cold_per_row × 1.2 falsely
                // exceeds the budget). Under-counting is bounded by the
                // duration-based spill detector.
                state.LastDeltaPerRow = perRow;
            }

            if (state.Settled) return;

            // Decide whether to grow for the next dispatch. Conservative:
            // require the predicted next-batch VRAM (with 20% headroom)
            // to fit in the remaining budget; otherwise settle here.
            int ceiling = ResolveCeiling(model);
            int nextBatch = state.CurrentBatch * 2;
            if (nextBatch > ceiling)
            {
                state.CurrentBatch = ceiling;
                state.Settled = true;
                DatumActivity.Operators.Trace(
                    $"batch-size-policy: model={model.Name} settled-at-ceiling={ceiling}");
                return;
            }

            if (state.LastDeltaPerRow <= 0)
            {
                // No usable VRAM delta this dispatch — common when the
                // model is already resident and ORT reuses its internal
                // arena for activations (NVML doesn't see allocations that
                // never grow the device-wide bookkeeping). Don't settle
                // here, or we lock at the cold-start batch=1 forever on a
                // warm engine. Blind-double up to BlindGrowthCeiling
                // instead — small enough to stay safe without any
                // measurement, large enough to reach a useful throughput.
                // Beyond that, we either need a real delta (next dispatch
                // may produce one) or the duration-based spill detector
                // pulls us back.
                if (nextBatch <= BlindGrowthCeiling)
                {
                    state.CurrentBatch = nextBatch;
                    DatumActivity.Operators.Trace(
                        $"batch-size-policy: model={model.Name} blind-ramp batch={batchSize}->{nextBatch} "
                        + "(no VRAM delta visible)");
                }
                else
                {
                    state.Settled = true;
                    DatumActivity.Operators.Trace(
                        $"batch-size-policy: model={model.Name} settled-blind at={state.CurrentBatch} "
                        + "(no VRAM delta visible, blind ceiling reached)");
                }
                return;
            }

            if (!_probe.TryGetUsage(out long used, out long total))
            {
                // Probe disappeared between dispatches. Settle at the
                // current batch — we can't safely extrapolate without a
                // VRAM ceiling.
                state.Settled = true;
                return;
            }

            long safety = SafetyMargin(total);
            long predicted = PredictedBytes(nextBatch, state.LastDeltaPerRow);
            if (used + predicted + safety <= total)
            {
                state.CurrentBatch = nextBatch;
                DatumActivity.Operators.Trace(
                    $"batch-size-policy: model={model.Name} ramp batch={batchSize}->{nextBatch} "
                    + $"perRow={state.LastDeltaPerRow}B vram={used}/{total}B");
            }
            else
            {
                state.Settled = true;
                DatumActivity.Operators.Trace(
                    $"batch-size-policy: model={model.Name} settled at={state.CurrentBatch} "
                    + $"perRow={state.LastDeltaPerRow}B vram={used}/{total}B predicted-next={predicted}B");
            }
        }
    }

    /// <summary>
    /// Hard upper bound on the doubling ramp. Models can pin their own
    /// ceiling via <see cref="IModel.PreferredBatchSize"/> (LLMs with
    /// KV-cache constraints, image generators with multi-MB per-row
    /// outputs); everything else uses <see cref="DefaultCeiling"/>.
    /// </summary>
    private static int ResolveCeiling(IModel model)
        => model.PreferredBatchSize is int p && p > 0 ? p : DefaultCeiling;

    /// <summary>
    /// VRAM the policy refuses to spend on a dispatch — leaves room for
    /// allocator fragmentation, ONNX kernel scratch, and concurrent
    /// queries on the same engine. Larger of 512 MB or 10% of device
    /// VRAM.
    /// </summary>
    private static long SafetyMargin(long totalVram)
        => Math.Max(512L * 1024 * 1024, totalVram / 10);

    /// <summary>
    /// Conservative prediction of a dispatch's VRAM cost: linear
    /// extrapolation of the last observed per-row delta plus 20%
    /// headroom. Over-counts modestly so we never spill, at the cost of
    /// occasionally leaving a halving of throughput on the table.
    /// </summary>
    private static long PredictedBytes(int batchSize, long lastDeltaPerRow)
        => (long)(batchSize * lastDeltaPerRow * 1.2);

    /// <summary>
    /// Per-model ramp state. Mutated under its own lock — multiple
    /// queries on the same engine can dispatch the same model
    /// concurrently and shouldn't race on the ramp transitions.
    /// </summary>
    private sealed class ModelState
    {
        public int CurrentBatch;
        public long LastDeltaPerRow;
        public bool Settled;
        /// <summary>
        /// Lowest per-row dispatch time we've observed for this model.
        /// Reference point for <see cref="SpillDetectionMultiplier"/>.
        /// Updated only on non-spill readings — a spill measurement
        /// should never lower the bar for what counts as healthy throughput.
        /// </summary>
        public double BestMsPerRow;
    }
}
