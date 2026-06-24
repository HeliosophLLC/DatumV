using Heliosoph.DatumV.GpuRuntime;
using Microsoft.AspNetCore.Mvc;
using Tapper;


namespace Heliosoph.DatumV.Web.Api;

// GPU acceleration: probe driver state, fetch the manifest, install /
// uninstall the deferred CUDA runtime bundle. Long-running install
// returns 202 immediately and streams progress via the SignalR hub —
// the Settings UI subscribes via state/gpu.ts handlers.
[ApiController]
[Route("api/gpu")]
public sealed class GpuController(
    GpuRuntimeProbe probe,
    CudaBundleManifestFetcher manifestFetcher,
    CudaBundleInstaller installer) : ControllerBase
{
    [HttpGet("status")]
    public async Task<GpuStatusDto> GetStatusAsync(CancellationToken ct)
    {
        GpuRuntimeStatus status = probe.Probe();
        CudaBundleManifest? manifest = await manifestFetcher.FetchAsync(ct).ConfigureAwait(false);

        CudaBundlePlatformEntryDto? availableEntry = null;
        if (manifest is not null && status.Platform is not null
            && manifest.Platforms.TryGetValue(status.Platform, out CudaBundlePlatformEntry? entry))
        {
            availableEntry = new CudaBundlePlatformEntryDto(
                entry.Url, entry.Sha256, entry.SizeBytes, entry.ExtractedSizeBytes);
        }

        bool updateAvailable = manifest is not null
            && status.InstalledBundleVersion is not null
            && manifest.Version != status.InstalledBundleVersion;

        // Compute capability gate. NVIDIA's cuDNN 9 docs say CC 6.0+ is
        // supported, but in practice cuDNN 9.x point releases have thinned
        // the precompiled-kernel set for Pascal (CC 6.x). Conv operations
        // in particular often lack a Pascal binary and fail at runtime
        // with `cudaErrorNoKernelImageForDevice` (the user-visible symptom:
        // "no kernel image is available for execution on the device"). For
        // a model-zoo app where most ONNX workloads exercise Conv, the
        // honest floor is Volta+ (CC 7.0). Pascal owners and below see the
        // "incompatible architecture" warning and get directed to the
        // standard installer (DirectML on Windows, Vulkan + CPU on Linux),
        // both of which work fine down to Maxwell.
        bool cudaCompatible = ParseComputeCapability(status.NvidiaComputeCapability) >= 7.0;

        return new GpuStatusDto(
            Platform: status.Platform,
            VariantSupportsCuda: GpuBuildVariant.SupportsCuda,
            HasNvidiaDriver: status.HasNvidiaDriver,
            NvidiaGpuName: status.NvidiaGpuName,
            NvidiaComputeCapability: status.NvidiaComputeCapability,
            CudaCompatible: cudaCompatible,
            InstalledVersion: status.InstalledBundleVersion,
            InstalledPath: status.InstalledBundlePath,
            AvailableVersion: manifest?.Version,
            AvailableEntry: availableEntry,
            UpdateAvailable: updateAvailable,
            IsInstalling: installer.IsInstalling,
            ActiveInstallVersion: installer.ActiveVersion);
    }

    // Parse "8.9" / "3.0" etc. into a double for comparison. Returns -1
    // when parsing fails (treats unknown CC as not-compatible so the UI
    // doesn't offer install to a machine we couldn't probe — better to
    // show "couldn't determine" than offer a likely-broken install).
    private static double ParseComputeCapability(string? cc)
        => double.TryParse(cc, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v)
            ? v
            : -1.0;

    [HttpPost("install")]
    public IActionResult StartInstall(CancellationToken ct)
    {
        bool started = installer.TryStartInstall(ct);
        return started
            ? Accepted()
            : Conflict(new { error = "An install is already running" });
    }

    [HttpPost("install/cancel")]
    public IActionResult CancelInstall()
    {
        installer.Cancel();
        return NoContent();
    }

    [HttpDelete("installed/{version}")]
    public IActionResult Uninstall(string version)
    {
        bool removed = installer.Uninstall(version);
        return removed ? NoContent() : NotFound();
    }
}

// Wire DTOs — flat shapes for TS codegen. Mirror the core types but
// without the IReadOnlyDictionary that's awkward to transpile.

[TranspilationSource]
public sealed record GpuStatusDto(
    string? Platform,
    // False on the `standard` build variant (no libonnxruntime_providers_cuda).
    // The Settings UI hides the GPU section entirely when this is false so
    // standard-variant users with NVIDIA hardware aren't offered an install
    // that won't enable GPU. They have to download the cuda variant instead.
    bool VariantSupportsCuda,
    bool HasNvidiaDriver,
    // Human-readable GPU name from nvidia-smi (e.g. "NVIDIA GeForce RTX 4090",
    // "NVIDIA GeForce GTX 880M"). Null when no NVIDIA driver or nvidia-smi
    // couldn't be invoked.
    string? NvidiaGpuName,
    // Compute capability as reported by nvidia-smi, e.g. "8.9", "3.0".
    // Null when driver detection succeeded but nvidia-smi didn't yield a
    // parseable value. The Settings UI uses this with CudaCompatible to
    // decide whether to offer install, show an "incompatible architecture"
    // warning, or fall through to the normal "no driver" path.
    string? NvidiaComputeCapability,
    // True iff the detected GPU's compute capability is ≥ 5.0 — the floor
    // for ORT.Gpu 1.25 + cuDNN 9. False on Kepler GPUs (CC 3.x) like the
    // GTX 880M / 780 / 750, and on any machine where the CC couldn't be
    // determined (defensive — better to redirect to the standard installer
    // than offer an install that won't accelerate anything).
    bool CudaCompatible,
    string? InstalledVersion,
    string? InstalledPath,
    string? AvailableVersion,
    CudaBundlePlatformEntryDto? AvailableEntry,
    bool UpdateAvailable,
    bool IsInstalling,
    string? ActiveInstallVersion);

[TranspilationSource]
public sealed record CudaBundlePlatformEntryDto(
    string Url,
    string Sha256,
    long SizeBytes,
    long ExtractedSizeBytes);
