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

        return new GpuStatusDto(
            Platform: status.Platform,
            HasNvidiaDriver: status.HasNvidiaDriver,
            InstalledVersion: status.InstalledBundleVersion,
            InstalledPath: status.InstalledBundlePath,
            AvailableVersion: manifest?.Version,
            AvailableEntry: availableEntry,
            UpdateAvailable: updateAvailable,
            IsInstalling: installer.IsInstalling,
            ActiveInstallVersion: installer.ActiveVersion);
    }

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
    bool HasNvidiaDriver,
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
