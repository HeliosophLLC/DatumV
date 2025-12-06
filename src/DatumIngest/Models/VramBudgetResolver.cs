using System.Diagnostics;
using System.Globalization;

namespace DatumIngest.Models;

/// <summary>
/// Detects total GPU VRAM via <c>nvidia-smi</c> and computes a budget for
/// <see cref="ModelResidencyManager"/>. Detection is best-effort — if
/// <c>nvidia-smi</c> isn't on PATH (no NVIDIA GPU, CPU-only host, or
/// driver not installed) the resolver returns a conservative fallback so
/// the catalog still constructs.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why detect at all.</strong> The default budget used to be
/// <see cref="ModelResidencyManager.UnlimitedBudget"/>, which preserved
/// the load-once-hold-forever shape of the early model zoo. That shape
/// breaks once the catalog grows past physical VRAM: CUDA happily
/// over-allocates into Windows' "shared GPU memory" (system RAM
/// reachable over PCIe), and inference falls off a ~30× performance
/// cliff because every kernel pays PCIe latency for half its weight
/// reads. Detecting VRAM and bounding the budget makes eviction kick in
/// before that cliff.
/// </para>
/// <para>
/// <strong>Why nvidia-smi specifically.</strong> WMI's
/// <c>Win32_VideoController.AdapterRAM</c> caps at 4 GiB (uint32) and
/// reports junk on modern cards. The CUDA runtime API would be more
/// accurate but adds a P/Invoke surface we don't otherwise need.
/// nvidia-smi's CSV output is stable, machine-parseable, and ships with
/// every NVIDIA driver — the right tool for "find out how much VRAM the
/// GPU has, once at startup."
/// </para>
/// </remarks>
public static class VramBudgetResolver
{
    /// <summary>
    /// Bytes left out of the budget for ONNX Runtime overhead, intermediate
    /// activation tensors, and CUDA kernel arenas. Empirically a 4 GiB
    /// margin is enough on top of the model weights themselves.
    /// </summary>
    public const long DefaultHeadroomBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>
    /// Conservative fallback when nvidia-smi isn't available. Picked to
    /// match a mid-tier GPU (16 GiB VRAM) minus headroom — large enough
    /// for SDXL-Turbo + Whisper + Llama 3.1 8B coexisting, small enough
    /// to keep the rest evicted.
    /// </summary>
    public const long DefaultFallbackBudgetBytes = 12L * 1024 * 1024 * 1024;

    /// <summary>
    /// Resolves a VRAM budget by querying <c>nvidia-smi</c> for total
    /// device memory and subtracting <paramref name="headroomBytes"/>.
    /// Returns <paramref name="fallbackBytes"/> if the query fails.
    /// </summary>
    /// <param name="headroomBytes">
    /// Bytes to leave outside the budget. Defaults to
    /// <see cref="DefaultHeadroomBytes"/>; tune up if you see ONNX
    /// Runtime allocation failures during inference, down if your zoo
    /// won't fit.
    /// </param>
    /// <param name="fallbackBytes">
    /// Budget to use when detection fails. Defaults to
    /// <see cref="DefaultFallbackBudgetBytes"/>.
    /// </param>
    public static long Resolve(
        long headroomBytes = DefaultHeadroomBytes,
        long fallbackBytes = DefaultFallbackBudgetBytes)
    {
        if (TryDetectTotalVramBytes(out long totalBytes))
        {
            // Always keep at least a 2 GiB budget, even on cards smaller
            // than the headroom — better to evict aggressively than to
            // refuse all loads.
            long minBudget = 2L * 1024 * 1024 * 1024;
            return Math.Max(minBudget, totalBytes - headroomBytes);
        }
        return fallbackBytes;
    }

    /// <summary>
    /// Best-effort VRAM detection. Returns true only when nvidia-smi
    /// produced a parseable byte count. Multi-GPU hosts return the first
    /// device's VRAM; the ONNX session and LLamaSharp default to GPU 0
    /// anyway, so that matches what the runtime will actually use.
    /// </summary>
    public static bool TryDetectTotalVramBytes(out long bytes)
    {
        bytes = 0;
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=memory.total --format=csv,noheader,nounits",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (process is null) return false;

            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(); } catch { /* best-effort */ }
                return false;
            }
            if (process.ExitCode != 0) return false;

            // Output is one line per GPU: "24576" (MiB). Take first GPU.
            string firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
            if (!long.TryParse(firstLine, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out long mib) || mib <= 0)
            {
                return false;
            }

            bytes = mib * 1024L * 1024L;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
