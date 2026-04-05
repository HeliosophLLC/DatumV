using System.Collections.Concurrent;

namespace DatumIngest.Models.Calibration;

/// <summary>
/// Per-model VRAM calibration: how many bytes the weights themselves
/// cost, and the total VRAM footprint (weights + activation peak) at
/// each calibrated batch size. Consumed by the batch-sizing policy to
/// pick the largest batch that fits in available VRAM without
/// exhausting the device.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Totals, not marginals.</strong> Earlier versions stored
/// activation deltas (<c>vramAfterDispatch - vramBeforeDispatch</c>),
/// but ORT's CUDA arena absorbs same-shape allocations between
/// consecutive dispatches: batch=2 reuses batch=1's pool blocks and the
/// snapshot delta reads zero even though batch=2 genuinely needs more
/// memory. The fix is to store the absolute peak VRAM observed during
/// a fresh-load dispatch — that's the real "this is how much memory
/// this batch size needs" number, independent of allocator
/// memoisation. Calibration evicts and reloads the model between ramp
/// steps to force fresh allocations; the policy reads
/// <c>total - weight_cost</c> to derive activation cost at fit-check
/// time.
/// </para>
/// <para>
/// <strong>Identity.</strong> The calibration is bound to a specific
/// model file via its SHA-256. If the file on disk changes, the catalog
/// re-hashes on next load and the curve is treated as <c>Stale</c> —
/// the calibration coordinator will recalibrate the next time the model
/// is acquired.
/// </para>
/// <para>
/// <strong>No online refinement.</strong> A real dispatch's
/// snapshot-based reading can't produce an absolute total (the arena
/// hides allocation growth), so the curve is written once per ramp and
/// only updated by an explicit recalibration. The policy layer still
/// monitors per-row duration; a sudden jump triggers
/// <see cref="RecordSpill"/> which drops the offending entry and marks
/// the calibration <see cref="State.Stale"/> so the next acquire forces
/// a fresh ramp.
/// </para>
/// </remarks>
public sealed class ModelCalibration
{
    private readonly object _lock = new();
    private readonly SortedDictionary<int, CalibrationEntry> _curve = [];

    /// <summary>
    /// Lifecycle status of a per-model calibration record.
    /// </summary>
    public enum State
    {
        /// <summary>No measurements yet; the calibration coordinator must
        /// run a ramp pass on next acquire.</summary>
        Uncalibrated,

        /// <summary>Curve populated and trusted; steady-state lookups
        /// proceed directly from the curve.</summary>
        Calibrated,

        /// <summary>A measurement disagreed with the curve beyond the
        /// drift tolerance, or a spill was detected. Treat as
        /// <see cref="Uncalibrated"/> for next acquire; the prior data
        /// remains queryable for diagnostics until overwritten.</summary>
        Stale,
    }

    /// <summary>
    /// The model file's SHA-256 hex digest at the time the curve was
    /// recorded. Used for invalidation when the on-disk file changes.
    /// </summary>
    public string FileSha256 { get; }

    /// <summary>
    /// One-shot VRAM cost of loading the model's weights (peak-sampled
    /// between <c>vramBeforeLoad</c> and the end of session init).
    /// Subtracted from <see cref="CalibrationEntry.TotalVramBytes"/> at
    /// fit-check time to derive the activation cost at a given batch
    /// size.
    /// </summary>
    public long WeightCostBytes { get; private set; }

    /// <summary>Current lifecycle status. See <see cref="State"/>.</summary>
    public State Status { get; private set; }

    /// <summary>
    /// Total VRAM (bytes) per batch size, ordered ascending. Each entry
    /// is the peak observed during a fresh-load dispatch at that batch
    /// size — i.e. weights + activations. Empty when <see cref="Status"/>
    /// is <see cref="State.Uncalibrated"/>.
    /// </summary>
    public IReadOnlyDictionary<int, CalibrationEntry> Curve
    {
        get
        {
            lock (_lock) return new Dictionary<int, CalibrationEntry>(_curve);
        }
    }

    /// <summary>
    /// Constructs a fresh calibration record for the given file hash.
    /// Starts in <see cref="State.Uncalibrated"/> with no curve entries.
    /// </summary>
    public ModelCalibration(string fileSha256)
    {
        FileSha256 = fileSha256;
        Status = State.Uncalibrated;
    }

    /// <summary>
    /// Constructs a calibration record from previously persisted data.
    /// Used by <c>CalibrationStore</c> on load. Sets <see cref="Status"/>
    /// to <see cref="State.Calibrated"/> when the curve is non-empty.
    /// </summary>
    public ModelCalibration(string fileSha256, long weightCostBytes, IEnumerable<KeyValuePair<int, CalibrationEntry>> curve)
    {
        FileSha256 = fileSha256;
        WeightCostBytes = weightCostBytes;
        foreach (KeyValuePair<int, CalibrationEntry> entry in curve)
        {
            _curve[entry.Key] = entry.Value;
        }
        Status = _curve.Count > 0 ? State.Calibrated : State.Uncalibrated;
    }

    /// <summary>
    /// Records the model's one-shot weight cost. Called once at load
    /// time, after <c>vramAfterLoad - vramBeforeLoad</c> has been
    /// measured. Replaces any prior value (a reload at a different driver
    /// version, say, may legitimately measure differently).
    /// </summary>
    public void SetWeightCost(long bytes)
    {
        lock (_lock) WeightCostBytes = bytes;
    }

    /// <summary>
    /// Inserts or replaces the calibration entry for a batch size with a
    /// fresh measurement. Called during the calibration ramp pass for
    /// each (1, 2, 4, 8, …) step. <paramref name="totalVramBytes"/> is the
    /// absolute peak VRAM observed during the dispatch (weights +
    /// activations); the coordinator evicts and reloads the model
    /// between steps so this number reflects a fresh allocation rather
    /// than an arena-absorbed delta. After the ramp completes, the
    /// curve is sealed by calling <see cref="MarkCalibrated"/>.
    /// </summary>
    public void Record(int batchSize, long totalVramBytes, DateTimeOffset measuredAt)
    {
        lock (_lock)
        {
            _curve[batchSize] = new CalibrationEntry(
                TotalVramBytes: totalVramBytes,
                ObservationCount: 1,
                LastValidatedAt: measuredAt);
        }
    }

    /// <summary>
    /// Promotes the curve from <see cref="State.Uncalibrated"/> to
    /// <see cref="State.Calibrated"/>. Called by the calibration
    /// coordinator after the ramp pass finishes successfully.
    /// </summary>
    public void MarkCalibrated()
    {
        lock (_lock)
        {
            if (_curve.Count > 0) Status = State.Calibrated;
        }
    }

    /// <summary>
    /// Returns the largest calibrated batch size whose activation cost
    /// fits the supplied <paramref name="availableActivationBytes"/>.
    /// Activation cost is derived as
    /// <c>entry.TotalVramBytes - WeightCostBytes</c> — the weights are
    /// already resident when the policy is asked, so only the
    /// dispatch's incremental allocation needs to fit in free VRAM.
    /// Returns <c>null</c> when no calibrated entry fits (caller should
    /// drop to batch=1 or fail). Walks the curve from largest to
    /// smallest.
    /// </summary>
    /// <param name="availableActivationBytes">
    /// Free VRAM available for activations on top of the resident
    /// weights, after subtracting the policy's safety margin.
    /// </param>
    public int? PickLargestFitting(long availableActivationBytes)
    {
        lock (_lock)
        {
            foreach (int batch in _curve.Keys.Reverse())
            {
                CalibrationEntry entry = _curve[batch];
                // Skip entries where the derived activation cost is
                // non-positive. A total ≤ weight_cost means either the
                // ramp's peak sampler missed (10 ms tick interval can
                // skip a fast dispatch entirely) or weight_cost is
                // stale. Either way the entry can't be trusted: the
                // predicate `0 <= availableActivationBytes` always
                // holds, so the batch would always "fit" regardless of
                // actual cost — exactly the foot-gun we're avoiding.
                long activation = entry.TotalVramBytes - WeightCostBytes;
                if (activation <= 0) continue;
                if (activation <= availableActivationBytes)
                {
                    return batch;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Drops the curve entry for <paramref name="spilledBatchSize"/> and
    /// every larger entry, then transitions to <see cref="State.Stale"/>.
    /// Called by the policy layer when duration-jump spill detection
    /// fires — the cliff is between the last known-good entry and the
    /// spilled one, so everything at or above the spilled size is
    /// suspect.
    /// </summary>
    public void RecordSpill(int spilledBatchSize)
    {
        lock (_lock)
        {
            List<int> toRemove = _curve.Keys.Where(k => k >= spilledBatchSize).ToList();
            foreach (int k in toRemove) _curve.Remove(k);
            Status = State.Stale;
        }
    }

    /// <summary>Explicitly drops the curve (manual RESET CALIBRATION).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _curve.Clear();
            WeightCostBytes = 0;
            Status = State.Uncalibrated;
        }
    }
}

/// <summary>
/// One row of a model's calibration curve.
/// </summary>
/// <param name="TotalVramBytes">
/// Absolute peak VRAM observed during a fresh-load dispatch at this
/// batch size — weights + activations combined. The policy derives
/// activation cost as <c>TotalVramBytes - WeightCostBytes</c> at the
/// fit-check call site.
/// </param>
/// <param name="ObservationCount">
/// How many dispatches have contributed to this entry. With totals
/// semantics this is always 1 (one fresh-load measurement per ramp
/// step); kept on the record for forward compatibility with future
/// confidence-weighted refinement.
/// </param>
/// <param name="LastValidatedAt">
/// UTC timestamp of the measurement.
/// </param>
public sealed record CalibrationEntry(
    long TotalVramBytes,
    int ObservationCount,
    DateTimeOffset LastValidatedAt);
