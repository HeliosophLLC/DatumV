// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON deserialization will not survive trimming

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
        return new GpuRuntimeStatus(
            Platform: CurrentPlatformId(),
            HasNvidiaDriver: hasDriver,
            InstalledBundleVersion: installed?.Version,
            InstalledBundlePath: installed?.Path);
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
                    File.ReadAllBytes(marker));
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
public sealed record GpuRuntimeStatus(
    string? Platform,
    bool HasNvidiaDriver,
    string? InstalledBundleVersion,
    string? InstalledBundlePath);
