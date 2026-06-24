// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON deserialization will not survive trimming

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Heliosoph.DatumV.GpuRuntime;

// Probes whether NVIDIA GPU acceleration is available and whether the
// deferred-install CUDA runtime bundle is on disk. Cheap to call — no
// network I/O, just dlopen + file lookups. Safe to invoke synchronously
// from the .NET startup path or repeatedly from a status endpoint.
public sealed class GpuRuntimeProbe
{
    private readonly ICudaBundleCacheLayout _layout;
    private readonly ILogger<GpuRuntimeProbe> _logger;

    public GpuRuntimeProbe(ICudaBundleCacheLayout layout, ILogger<GpuRuntimeProbe> logger)
    {
        _layout = layout;
        _logger = logger;
    }

    public GpuRuntimeStatus Probe()
    {
        bool hasDriver = DetectNvidiaDriver();
        InstalledBundle? installed = TryReadInstalled();
        (string? gpuName, string? computeCapability) = hasDriver
            ? DetectNvidiaGpu()
            : (null, null);
        return new GpuRuntimeStatus(
            Platform: CurrentPlatformId(),
            HasNvidiaDriver: hasDriver,
            NvidiaGpuName: gpuName,
            NvidiaComputeCapability: computeCapability,
            InstalledBundleVersion: installed?.Version,
            InstalledBundlePath: installed?.Path);
    }

    // Queries nvidia-smi for the highest-CC GPU on the system. Returns
    // (name, "major.minor") on success; (null, null) when nvidia-smi isn't
    // available or output didn't parse. nvidia-smi is shipped with every
    // NVIDIA driver install, so its absence after DetectNvidiaDriver()
    // succeeded is unusual but not impossible (driver-only minimal install).
    //
    // Output shape (one line per GPU):
    //   NVIDIA GeForce RTX 4090, 8.9
    //   NVIDIA GeForce GTX 880M, 3.0
    //
    // Multi-GPU machines: report the highest CC + its name. That's what
    // matters for "will CUDA inference work" — if any GPU on the box is
    // CC ≥ 5.0, the user can route work to it.
    private (string? Name, string? ComputeCapability) DetectNvidiaGpu()
    {
        try
        {
            using Process proc = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=name,compute_cap --format=csv,noheader,nounits",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            if (!proc.Start()) return (null, null);
            // 3s is generous — nvidia-smi normally returns in tens of ms.
            // Cap so the probe can't hang the status endpoint if the
            // driver is in a wedged state.
            if (!proc.WaitForExit(3_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return (null, null);
            }
            if (proc.ExitCode != 0) return (null, null);
            string stdout = proc.StandardOutput.ReadToEnd();

            string? bestName = null;
            double bestCc = -1.0;
            string? bestCcStr = null;
            foreach (string line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int comma = line.LastIndexOf(',');
                if (comma <= 0) continue;
                string name = line[..comma].Trim();
                string ccStr = line[(comma + 1)..].Trim();
                if (!double.TryParse(ccStr, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double cc))
                    continue;
                if (cc > bestCc)
                {
                    bestCc = cc;
                    bestCcStr = ccStr;
                    bestName = name;
                }
            }
            return (bestName, bestCcStr);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "nvidia-smi probe threw; reporting unknown GPU capability");
            return (null, null);
        }
    }

    // Returns "linux-x64" / "win-x64" / null. The cuda bundle is only
    // built + shipped for those two; macOS NVIDIA support ended years
    // ago and aarch64 NVIDIA is Jetson-only (out of scope here).
    public static string? CurrentPlatformId()
    {
        if (!RuntimeInformation.OSArchitecture.Equals(Architecture.X64))
            return null;
        if (OperatingSystem.IsLinux()) return "linux-x64";
        if (OperatingSystem.IsWindows()) return "win-x64";
        return null;
    }

    private bool DetectNvidiaDriver()
    {
        try
        {
            // libcuda.so.1 (Linux) / nvcuda.dll (Windows) is the NVIDIA
            // driver-shipped CUDA driver API, distinct from the CUDA
            // runtime libcudart.so.12 we bundle ourselves. Present iff an
            // NVIDIA driver is installed; missing on AMD/Intel/no-GPU
            // systems. Load and immediately free — we only need the
            // detection signal, not the handle.
            string? probeName = OperatingSystem.IsLinux() ? "libcuda.so.1"
                : OperatingSystem.IsWindows() ? "nvcuda.dll"
                : null;
            if (probeName is null) return false;
            if (!NativeLibrary.TryLoad(probeName, out IntPtr handle)) return false;
            NativeLibrary.Free(handle);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NVIDIA driver probe threw; assuming no driver");
            return false;
        }
    }

    // CudaBundleInstaller writes the marker with camelCase keys (System.Text.Json
    // emits anonymous-type property names as-is, and the writer uses lowercase
    // names). System.Text.Json's default reader is case-sensitive, so a record
    // with PascalCase properties produces null fields. Match the writer's casing.
    private static readonly JsonSerializerOptions InstalledMarkerJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private InstalledBundle? TryReadInstalled()
    {
        string root = _layout.CacheRoot;
        if (!Directory.Exists(root)) return null;

        // Look for an `installed.json` marker in any version subdir. The
        // installer writes this last (atomically) so its presence
        // guarantees the directory contents are complete.
        foreach (string versionDir in Directory.EnumerateDirectories(root))
        {
            string marker = Path.Combine(versionDir, "installed.json");
            if (!File.Exists(marker)) continue;
            try
            {
                InstalledMarker? data = JsonSerializer.Deserialize<InstalledMarker>(
                    File.ReadAllBytes(marker), InstalledMarkerJsonOpts);
                if (data is { Version.Length: > 0 })
                {
                    return new InstalledBundle(data.Version, versionDir);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not read installed marker at {Path}; ignoring", marker);
            }
        }
        return null;
    }

    private sealed record InstalledBundle(string Version, string Path);
    private sealed record InstalledMarker(string Version, string Sha256, DateTime InstalledAtUtc);
}

// Snapshot of what the probe found. Consumed by the Web API and surfaced
// to the Settings UI. Renderer uses Platform to decide whether to even
// show the GPU section ("not supported on this OS" if Platform == null).
//
// NvidiaGpuName / NvidiaComputeCapability are only populated when an
// NVIDIA driver is installed AND nvidia-smi runs cleanly; they're nullable
// to distinguish "no driver" from "driver present but capability unknown".
public sealed record GpuRuntimeStatus(
    string? Platform,
    bool HasNvidiaDriver,
    string? NvidiaGpuName,
    string? NvidiaComputeCapability,
    string? InstalledBundleVersion,
    string? InstalledBundlePath);
