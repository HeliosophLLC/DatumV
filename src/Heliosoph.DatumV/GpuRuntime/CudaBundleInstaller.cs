// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

using System.Text.Json;
using Heliosoph.DatumV.ModelLibrary;
using Microsoft.Extensions.Logging;

namespace Heliosoph.DatumV.GpuRuntime;

// Orchestrates the deferred-bundle install: resolve manifest -> download
// bundle (resumable via HTTP Range) -> verify SHA-256 -> extract tar.zst
// to a staging dir -> atomically rename into final version dir -> write
// installed.json marker -> emit progress through ICudaBundleInstallProgressReporter.
//
// Concurrency: at most one install runs at a time per host. Subsequent
// Install calls return early without starting a duplicate; the Settings
// UI shows running progress via the SignalR stream.
//
// Resumability: a partially-downloaded .part file in the cache root is
// preserved across cancellation / app restart; the next Install call
// resumes from where it stopped.
public sealed class CudaBundleInstaller
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    private readonly HttpClient _http;
    private readonly CudaBundleManifestFetcher _manifestFetcher;
    private readonly ICudaBundleCacheLayout _layout;
    private readonly ICudaBundleInstallProgressReporter _reporter;
    private readonly ILogger<CudaBundleInstaller> _logger;

    private readonly SemaphoreSlim _installGate = new(1, 1);
    private CancellationTokenSource? _activeCts;
    private string? _activeVersion;

    public CudaBundleInstaller(
        HttpClient http,
        CudaBundleManifestFetcher manifestFetcher,
        ICudaBundleCacheLayout layout,
        ICudaBundleInstallProgressReporter reporter,
        ILogger<CudaBundleInstaller> logger)
    {
        _http = http;
        _manifestFetcher = manifestFetcher;
        _layout = layout;
        _reporter = reporter;
        _logger = logger;
    }

    /// <summary>True iff an install is currently in flight.</summary>
    public bool IsInstalling => _activeVersion is not null;

    /// <summary>The version currently being installed, or null when idle.</summary>
    public string? ActiveVersion => _activeVersion;

    /// <summary>
    /// Begin an install. Returns true if started; false if one was already
    /// running. Runs the actual work on a background task — callers should
    /// observe progress via the configured reporter, not by awaiting.
    /// </summary>
    public bool TryStartInstall(CancellationToken cancellationToken)
    {
        if (!_installGate.Wait(0)) return false;

        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeCts = cts;
        // _activeVersion is set inside RunAsync once we have the manifest.

        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CUDA bundle install crashed");
            }
            finally
            {
                _activeVersion = null;
                _activeCts = null;
                cts.Dispose();
                _installGate.Release();
            }
        }, cts.Token);
        return true;
    }

    /// <summary>Cancel an in-flight install. No-op if none is running.</summary>
    public void Cancel()
    {
        try { _activeCts?.Cancel(); }
        catch (ObjectDisposedException) { /* race with completion */ }
    }

    /// <summary>
    /// Remove an installed bundle from disk. Safe to call whether or not
    /// the bundle exists; returns true if anything was actually removed.
    /// </summary>
    public bool Uninstall(string version)
    {
        string dir = _layout.VersionDir(version);
        if (!Directory.Exists(dir)) return false;
        Directory.Delete(dir, recursive: true);
        return true;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        CudaBundleManifest? manifest = await _manifestFetcher.FetchAsync(ct, forceRefresh: true)
            .ConfigureAwait(false);
        if (manifest is null)
        {
            await EmitFailedAsync("unknown", "Could not fetch manifest", ct).ConfigureAwait(false);
            return;
        }

        string? platform = GpuRuntimeProbe.CurrentPlatformId();
        if (platform is null)
        {
            await EmitFailedAsync(manifest.Version, "Unsupported platform/arch", ct)
                .ConfigureAwait(false);
            return;
        }
        if (!manifest.Platforms.TryGetValue(platform, out CudaBundlePlatformEntry? entry))
        {
            await EmitFailedAsync(manifest.Version,
                $"Manifest has no entry for platform '{platform}'", ct).ConfigureAwait(false);
            return;
        }

        _activeVersion = manifest.Version;

        await _reporter.OnStartedAsync(
            new CudaBundleInstallStarted(manifest.Version, entry.SizeBytes), ct)
            .ConfigureAwait(false);

        try
        {
            Directory.CreateDirectory(_layout.CacheRoot);
            string downloadPath = _layout.DownloadStagingPath(manifest.Version);

            // ── Download ─────────────────────────────────────────────────
            Progress<DownloadByteProgress> dlProgress = new(p =>
            {
                _ = _reporter.OnDownloadProgressAsync(
                    new CudaBundleDownloadProgress(
                        manifest.Version,
                        p.BytesRead,
                        p.BytesTotal ?? entry.SizeBytes),
                    ct);
            });
            string actualSha = await HttpFileDownloader.DownloadAsync(
                _http, entry.Url, downloadPath, dlProgress, _logger, ct)
                .ConfigureAwait(false);

            // ── Verify ───────────────────────────────────────────────────
            if (!string.Equals(actualSha, entry.Sha256.ToLowerInvariant(), StringComparison.Ordinal))
            {
                // SHA mismatch: delete the bad download so the next attempt
                // re-fetches from zero instead of resuming garbage.
                TryDelete(downloadPath);
                await EmitFailedAsync(manifest.Version,
                    $"SHA-256 mismatch: expected {entry.Sha256}, got {actualSha}", ct)
                    .ConfigureAwait(false);
                return;
            }

            // ── Extract ──────────────────────────────────────────────────
            await _reporter.OnExtractStartedAsync(
                new CudaBundleExtractStarted(manifest.Version), ct).ConfigureAwait(false);

            string extractStaging = _layout.ExtractStagingDir(manifest.Version);
            // Re-extract over an existing staging dir from a crashed prior
            // run; safer than trying to verify partial extract state.
            if (Directory.Exists(extractStaging))
                Directory.Delete(extractStaging, recursive: true);

            Progress<TarZstExtractProgress> exProgress = new(p =>
            {
                _ = _reporter.OnExtractProgressAsync(
                    new CudaBundleExtractProgress(
                        manifest.Version, p.FilesExtracted, p.TotalFiles, p.BytesExtracted),
                    ct);
            });
            TarZstExtractResult result = await TarZstExtractor.ExtractAsync(
                downloadPath, extractStaging,
                totalFiles: 0,  // tar is single-pass; we don't pre-count
                exProgress, _logger, ct).ConfigureAwait(false);

            // ── Write marker + atomic-move into final location ───────────
            string installedMarker = Path.Combine(extractStaging, "installed.json");
            await File.WriteAllBytesAsync(
                installedMarker,
                JsonSerializer.SerializeToUtf8Bytes(new
                {
                    version = manifest.Version,
                    sha256 = entry.Sha256,
                    installedAtUtc = DateTime.UtcNow,
                    fileCount = result.FilesExtracted,
                    bytes = result.BytesExtracted,
                }, JsonOpts),
                ct).ConfigureAwait(false);

            string finalDir = _layout.VersionDir(manifest.Version);
            if (Directory.Exists(finalDir))
                Directory.Delete(finalDir, recursive: true);
            Directory.Move(extractStaging, finalDir);

            // Successful install: the .tar.zst is no longer needed.
            TryDelete(downloadPath);

            // GC older installed versions in the cache root — keeps disk
            // usage bounded if the user upgrades the bundle multiple times.
            CollectStaleVersions(currentlyInstalled: manifest.Version);

            await _reporter.OnInstalledAsync(
                new CudaBundleInstalled(manifest.Version, finalDir), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // User cancellation is not a failure; staging artefacts are
            // preserved so the next attempt resumes.
            _logger.LogInformation("CUDA bundle install cancelled at version {Version}", manifest.Version);
            throw;
        }
        catch (Exception ex)
        {
            await EmitFailedAsync(manifest.Version, ex.Message, ct).ConfigureAwait(false);
            throw;
        }
    }

    private void CollectStaleVersions(string currentlyInstalled)
    {
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(_layout.CacheRoot))
            {
                string name = Path.GetFileName(dir);
                // Don't touch staging dirs (prefixed with '.') or the
                // version we just installed.
                if (name.StartsWith('.')) continue;
                if (name == currentlyInstalled) continue;
                _logger.LogInformation("Removing stale CUDA bundle version {Version}", name);
                try { Directory.Delete(dir, recursive: true); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to remove stale CUDA bundle dir {Dir}", dir);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stale CUDA bundle GC threw; ignoring");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }

    private async ValueTask EmitFailedAsync(string version, string error, CancellationToken ct)
    {
        _logger.LogError("CUDA bundle install failed: {Error}", error);
        try
        {
            await _reporter.OnFailedAsync(
                new CudaBundleInstallFailed(version, error), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reporter OnFailedAsync threw");
        }
    }
}
