namespace Heliosoph.DatumV.Models.Calibration;

/// <summary>
/// Observer interface for <see cref="CalibrationCoordinator"/> ramp
/// lifecycle events. Hosts surfacing calibration state to a UI (a
/// status-bar chip that flashes while a ramp is in progress, an admin
/// view that streams curve data as it's recorded) implement this and
/// register via <see cref="ModelCatalog.AddCalibrationObserver"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Calling convention.</strong> Methods are invoked from the
/// coordinator's ramp task — off the caller's continuation chain, but
/// still inline within the ramp. Implementations MUST return quickly
/// and MUST NOT block; a slow observer extends every ramp step's
/// measured duration and skews the per-step timing the spill detector
/// depends on.
/// </para>
/// <para>
/// <strong>Lifecycle.</strong> A successful ramp emits
/// <see cref="OnRampStarted"/>, one <see cref="OnRampStep"/> per
/// measured batch size, and finally <see cref="OnRampCompleted"/>.
/// Early termination emits <see cref="OnRampHalted"/> in place of
/// <see cref="OnRampCompleted"/>; the curve is sealed at whatever
/// batches were measured before the halt. Spill detection routes
/// through <see cref="OnRampHalted"/> with a spill-tagged reason.
/// </para>
/// </remarks>
public interface ICalibrationObserver
{
    /// <summary>
    /// Fires after the per-model calibration gate has been acquired and
    /// the ramp has chosen to proceed. <paramref name="fingerprint"/> is
    /// the model file's content hash — useful for UIs that want to
    /// disambiguate "fresh calibration on a new file" from
    /// "recalibration after spill detection."
    /// </summary>
    void OnRampStarted(string modelName, string fingerprint);

    /// <summary>
    /// Fires once per ramp step after the dispatch completes and the
    /// peak-sampled total has been recorded into the curve. UI surfaces
    /// can stream these to draw a live curve as it builds.
    /// </summary>
    void OnRampStep(string modelName, int batchSize, long totalVramBytes, double dispatchMs);

    /// <summary>
    /// Fires when the ramp exits before completing every batch size in
    /// <see cref="CalibrationCoordinator.DefaultRampBatchSizes"/>. The
    /// curve is still valid up to <paramref name="lastBatchSize"/>;
    /// <see cref="HaltReason"/> distinguishes look-ahead-projected halts
    /// (model would have exceeded the budget) from duration-spill halts
    /// (we hit a soft cliff before allocator growth caught it).
    /// </summary>
    void OnRampHalted(string modelName, int lastBatchSize, HaltReason reason);

    /// <summary>
    /// Fires when the ramp ran every batch size without halting.
    /// <paramref name="entryCount"/> equals
    /// <see cref="CalibrationCoordinator.DefaultRampBatchSizes"/>.Count
    /// in the happy path.
    /// </summary>
    void OnRampCompleted(string modelName, int entryCount);
}

/// <summary>
/// Why a calibration ramp halted before measuring every batch size.
/// </summary>
public enum HaltReason
{
    /// <summary>
    /// Look-ahead projected that the next doubling would exceed device
    /// VRAM. Predictive halt — no failure occurred, the ramp just
    /// stopped before attempting an out-of-budget batch.
    /// </summary>
    LookAheadProjection,

    /// <summary>
    /// Duration-based spill detection — the current step's per-row time
    /// exceeded the best-observed by the spill threshold, indicating
    /// activations had begun spilling into shared GPU memory. The
    /// offending entry and everything above it are dropped from the
    /// curve and the calibration is marked
    /// <see cref="ModelCalibration.State.Stale"/>.
    /// </summary>
    DurationSpill,

    /// <summary>
    /// The caller's dispatch delegate threw — typically because the
    /// query that triggered calibration errored mid-ramp. The curve is
    /// sealed at whatever batches measured cleanly before the throw;
    /// the underlying exception is re-thrown to the awaiting query
    /// after observers are notified, so the chip can clear without
    /// swallowing the error.
    /// </summary>
    DispatchError,
}
