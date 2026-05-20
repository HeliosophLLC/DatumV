namespace Heliosoph.DatumV.Diagnostics;

/// <summary>
/// Background NVML poll that captures peak VRAM usage during a
/// synchronous-or-async block of work. Used by calibration to record
/// peak activation cost rather than the post-dispatch persistent delta —
/// NVML before/after samples miss transient peaks because GPU memory
/// freed by ONNX Runtime returns to the device's pool before the
/// "after" snapshot.
/// </summary>
/// <remarks>
/// Cadence is 10 ms. NVML reads cost microseconds (no kernel transition,
/// no PCIe traffic), so 100 samples/sec is cheap. A heavier read schedule
/// wouldn't catch sub-10ms peaks anyway — CUDA kernel launches happen
/// faster than NVML's reporting granularity. 10 ms hits a reasonable
/// sweet spot for typical inference dispatches (50 ms to multiple
/// seconds).
/// </remarks>
public static class VramPeakSampler
{
    /// <summary>
    /// Starts a background sampler that polls
    /// <see cref="VramProbe.TryGetUsage"/> until cancellation, invoking
    /// <paramref name="onSample"/> for each successful reading. Caller is
    /// responsible for awaiting the returned task after cancelling so
    /// all writes from <paramref name="onSample"/> are visible.
    /// </summary>
    /// <remarks>
    /// Probe failures are silently skipped — there's nothing useful to do
    /// with an unavailable probe during a polling loop; the caller's
    /// `vramBefore` reading already gated the peak-tracking decision.
    /// </remarks>
    public static Task StartAsync(CancellationToken cancellationToken, Action<long> onSample)
    {
        return Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (VramProbe.TryGetUsage(out long used, out _))
                    {
                        onSample(used);
                    }
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on the stop path; nothing to surface.
            }
        });
    }
}
