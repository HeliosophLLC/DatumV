using System.Runtime.InteropServices;

namespace DatumIngest.Diagnostics;

/// <summary>
/// Cheap, periodic VRAM-usage probe wrapping NVIDIA's NVML library
/// (<c>nvml.dll</c> on Windows). One-time lazy init at first call; each
/// subsequent <see cref="TryGetUsage"/> is microseconds — the same source
/// of truth <c>nvidia-smi</c> reads from, without the process-spawn cost.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Graceful fallback.</strong> Hosts without an NVIDIA GPU
/// (no driver installed, CPU-only build server, AMD/Intel GPU) return
/// <see langword="false"/> from every call. The probe never throws after
/// initialization, never logs noise, and never retries init after a
/// failure — one failed init poisons the state for the process lifetime.
/// </para>
/// <para>
/// <strong>Cost.</strong> NVML's <c>nvmlDeviceGetMemoryInfo</c> reads from
/// the driver's already-resident allocator bookkeeping; no kernel
/// transitions, no PCIe traffic. Empirically 5-20µs per call on consumer
/// hardware — safe to invoke at the 1Hz memory-sample cadence (and at
/// higher rates if needed). Compare to <c>nvidia-smi</c> at 50-200ms per
/// call due to process spawn + CSV parsing.
/// </para>
/// <para>
/// <strong>What it reports.</strong> "Used" matches what <c>nvidia-smi</c>
/// shows — global device memory consumed across every process (this
/// engine + browser + game + other ML apps), not just our own
/// allocations. That's the right number for "am I about to spill into
/// shared memory" decisions; it's the budget the GPU's allocator
/// actually has to work with.
/// </para>
/// <para>
/// <strong>Multi-GPU.</strong> Reports device 0 only. ONNX Runtime and
/// LlamaSharp both default to device 0 unless explicitly configured, so
/// this matches the runtime's actual residency footprint. Multi-device
/// hosts that want per-device sampling can extend; the engine doesn't
/// yet have a "current device" concept to thread through.
/// </para>
/// <para>
/// <strong>Windows-only for v1.</strong> Linux's NVML library name is
/// <c>libnvidia-ml.so.1</c> rather than <c>libnvml.so</c>, requiring an
/// <see cref="NativeLibrary"/> resolver shim. Adding that is one method
/// when needed.
/// </para>
/// </remarks>
public static class VramProbe
{
    // 0 = uninitialised, 1 = ready (init succeeded), 2 = unavailable
    // (NVML not present, init failed, or device 0 unreadable). The state
    // transition from 0 → 1/2 happens exactly once per process; concurrent
    // first callers race through the lock and the loser observes the
    // settled state on its second read.
    private static int _state;
    private static IntPtr _device;
    private static readonly Lock _initLock = new();

    /// <summary>
    /// Attempts to read the current VRAM usage for GPU 0. Returns
    /// <see langword="false"/> when NVML isn't available or the read
    /// failed — callers should treat that as "unknown" rather than "zero
    /// used."
    /// </summary>
    /// <param name="usedBytes">Bytes currently allocated on the device, system-wide.</param>
    /// <param name="totalBytes">Device VRAM capacity.</param>
    public static bool TryGetUsage(out long usedBytes, out long totalBytes)
    {
        if (_state == 0)
        {
            lock (_initLock)
            {
                if (_state == 0) Initialize();
            }
        }

        if (_state != 1)
        {
            usedBytes = 0;
            totalBytes = 0;
            return false;
        }

        try
        {
            if (Nvml.nvmlDeviceGetMemoryInfo(_device, out NvmlMemory mem) != 0)
            {
                usedBytes = 0;
                totalBytes = 0;
                return false;
            }
            usedBytes = (long)mem.Used;
            totalBytes = (long)mem.Total;
            return true;
        }
        catch (DllNotFoundException)
        {
            // NVML was loadable when we initialised but isn't now (DLL
            // unloaded, driver service restarted). Demote to permanent
            // unavailable rather than retrying — restarting NVML mid-
            // process introduces failure modes we don't want to debug.
            _state = 2;
            usedBytes = 0;
            totalBytes = 0;
            return false;
        }
    }

    private static void Initialize()
    {
        // Windows-only for v1 — Linux needs an NativeLibrary resolver
        // for the different library name (libnvidia-ml.so.1). Adding
        // that is a small follow-up when the engine targets Linux.
        if (!OperatingSystem.IsWindows())
        {
            _state = 2;
            return;
        }

        try
        {
            if (Nvml.nvmlInit_v2() != 0)
            {
                _state = 2;
                return;
            }
            if (Nvml.nvmlDeviceGetHandleByIndex_v2(0, out IntPtr device) != 0)
            {
                _state = 2;
                return;
            }
            _device = device;
            _state = 1;
        }
        catch (DllNotFoundException)
        {
            // No NVIDIA driver on this host. Quiet permanent fallback.
            _state = 2;
        }
        catch
        {
            // Any other init failure — bad NVML version, ABI mismatch,
            // etc. Same disposition: don't crash, don't retry.
            _state = 2;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    /// <summary>
    /// P/Invoke surface for NVML. Function names match NVIDIA's headers
    /// verbatim; entry points are stable across NVML 1.x — 12.x.
    /// </summary>
    private static class Nvml
    {
        [DllImport("nvml.dll")]
        public static extern int nvmlInit_v2();

        [DllImport("nvml.dll")]
        public static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

        [DllImport("nvml.dll")]
        public static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);
    }
}
